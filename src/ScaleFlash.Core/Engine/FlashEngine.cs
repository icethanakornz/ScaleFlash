using System.Collections.Concurrent;
using System.Diagnostics;
using ScaleFlash.Core.IO;
using ScaleFlash.Core.Native;

namespace ScaleFlash.Core.Engine;

/// <summary>
/// Copies <c>length</c> bytes from a source device to a target device.
/// A reader thread prefetches aligned chunks while the calling thread writes, so the card never
/// waits for the image file. Optionally skips chunks the card already holds and verifies afterwards.
/// </summary>
public static class FlashEngine
{
    private const int ProtectBytes = 4096;

    public static FlashResult Run(IBlockDevice source, long length, IBlockDevice target, FlashOptions options,
        IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        int align = Math.Max(Math.Max(source.SectorSize, target.SectorSize), 512);
        if (options.ChunkSize <= 0 || options.ChunkSize % AlignedBuffer.Alignment != 0 || options.ChunkSize % align != 0)
            throw new ArgumentException("Chunk size must be a multiple of 4096 and of the sector size.");
        long total = (length + align - 1) / align * align;
        if (total > target.Length)
            throw new ArgumentException($"Target ({target.Length} bytes) is smaller than the data ({total} bytes).");

        var state = new RunState(source, length, target, options, total, progress);
        var totalWatch = Stopwatch.StartNew();
        try
        {
            state.Report(FlashPhase.Preparing, 0, null, force: true);
            var writeWatch = Stopwatch.StartNew();
            var written = state.WritePhase(ct);
            writeWatch.Stop();
            state.WriteDuration = writeWatch.Elapsed;

            state.Report(FlashPhase.Finishing, state.PhaseTotal, "กำลังบันทึกข้อมูลที่ค้างในเครื่องอ่านการ์ด", force: true);
            target.Flush();

            if (options.Verify && written.Count > 0)
            {
                var verifyWatch = Stopwatch.StartNew();
                state.VerifyPhase(written, ct);
                state.VerifyDuration = verifyWatch.Elapsed;
            }

            state.Report(FlashPhase.Completed, state.PhaseTotal, null, force: true);
            return state.Result(totalWatch.Elapsed, success: true);
        }
        catch (OperationCanceledException)
        {
            state.Report(FlashPhase.Cancelled, state.LastDone, null, force: true);
            return state.Result(totalWatch.Elapsed, success: false) with { Cancelled = true, ErrorMessage = "ยกเลิกแล้ว" };
        }
        catch (VerifyMismatchException ex)
        {
            state.Report(FlashPhase.Failed, state.LastDone, null, force: true);
            return state.Result(totalWatch.Elapsed, success: false) with
            {
                ErrorMessage = ErrorText.ForVerifyMismatch(ex.Offset),
                FailedOffset = ex.Offset,
            };
        }
        catch (SourceReadException ex)
        {
            state.Report(FlashPhase.Failed, state.LastDone, null, force: true);
            return state.Result(totalWatch.Elapsed, success: false) with
            {
                ErrorMessage = ErrorText.ForIo(ex.Inner, isSource: true),
                Win32Error = ex.Inner.Win32Error,
                FailedOffset = ex.Inner.Offset,
            };
        }
        catch (DeviceIoException ex)
        {
            state.Report(FlashPhase.Failed, state.LastDone, null, force: true);
            return state.Result(totalWatch.Elapsed, success: false) with
            {
                ErrorMessage = ErrorText.ForIo(ex, isSource: false),
                Win32Error = ex.Win32Error,
                FailedOffset = ex.Offset,
            };
        }
    }

    private sealed class RunState(IBlockDevice source, long sourceLength, IBlockDevice target, FlashOptions options,
        long total, IProgress<FlashProgress>? progress)
    {
        private readonly int _chunk = options.ChunkSize;
        private readonly Stopwatch _phaseWatch = new();
        private readonly Queue<(double T, long Done)> _samples = new();
        private FlashPhase _phase;
        private long _lastReportTicks;
        private double _speed;

        public long BytesWritten, BytesSkipped, BytesVerified, LastDone, PhaseTotal;
        private readonly long _total = total;
        public TimeSpan WriteDuration, VerifyDuration;

        private int ChunkCount => (int)((_total + _chunk - 1) / _chunk);
        private long ChunkOffset(int i) => (long)i * _chunk;
        private int ChunkLength(int i) => (int)Math.Min(_chunk, _total - ChunkOffset(i));

        /// <summary>Returns the indices of chunks that were actually written.</summary>
        public List<int> WritePhase(CancellationToken ct)
        {
            BeginPhase(FlashPhase.Writing, _total);
            int n = ChunkCount;
            bool protect = options.ProtectAgainstInterruption && n > 1;
            var order = new List<int>(n);
            for (int i = protect ? 1 : 0; i < n; i++) order.Add(i);
            if (protect)
            {
                order.Add(0);
                using var zero = new AlignedBuffer(AlignedBuffer.Alignment);
                WriteWithRetry(zero.Slice(0, Math.Min(ProtectBytes, ChunkLength(0))), 0, ct);
            }

            var written = new List<int>(n);
            using var compareBuf = options.SkipIdenticalChunks ? new AlignedBuffer(_chunk) : null;
            var cmp = new AdaptiveCompare(options.SkipIdenticalChunks);
            long done = 0;

            using var reader = new ChunkReader(source, sourceLength, order, ChunkOffset, ChunkLength, _chunk, options.PrefetchBuffers, ct);
            foreach (var (index, buffer, len) in reader.Consume())
            {
                long offset = ChunkOffset(index);
                var data = buffer.Slice(0, len);
                bool forceWrite = protect && index == 0; // its first 4 KiB were zeroed on purpose
                bool same = false;
                if (!forceWrite && cmp.ShouldCompare())
                {
                    try
                    {
                        var current = compareBuf!.Slice(0, len);
                        target.Read(current, offset);
                        same = current.SequenceEqual(data);
                    }
                    catch (DeviceIoException) { same = false; }
                    cmp.Record(same);
                }

                if (same)
                {
                    BytesSkipped += len;
                }
                else
                {
                    WriteWithRetry(data, offset, ct);
                    BytesWritten += len;
                    written.Add(index);
                }
                reader.Release(buffer);
                done += len;
                Report(FlashPhase.Writing, done, null);
            }
            return written;
        }

        public void VerifyPhase(List<int> written, CancellationToken ct)
        {
            var order = written.OrderBy(i => i).ToList();
            long verifyTotal = order.Sum(i => (long)ChunkLength(i));
            BeginPhase(FlashPhase.Verifying, verifyTotal);
            using var check = new AlignedBuffer(_chunk);
            long done = 0;
            using var reader = new ChunkReader(source, sourceLength, order, ChunkOffset, ChunkLength, _chunk, options.PrefetchBuffers, ct);
            foreach (var (index, buffer, len) in reader.Consume())
            {
                long offset = ChunkOffset(index);
                var actual = check.Slice(0, len);
                target.Read(actual, offset);
                var expected = buffer.Slice(0, len);
                if (!actual.SequenceEqual(expected))
                {
                    int at = 0;
                    while (at < len && actual[at] == expected[at]) at++;
                    throw new VerifyMismatchException(offset + at);
                }
                reader.Release(buffer);
                done += len;
                BytesVerified += len;
                Report(FlashPhase.Verifying, done, null);
            }
        }

        private void WriteWithRetry(ReadOnlySpan<byte> data, long offset, CancellationToken ct)
        {
            for (int attempt = 0; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    target.Write(data, offset);
                    return;
                }
                catch (DeviceIoException ex) when (attempt < options.MaxRetries && ErrorText.IsRetryable(ex.Win32Error))
                {
                    Report(_phase, LastDone, $"เขียนไม่สำเร็จ กำลังลองใหม่ ({attempt + 1}/{options.MaxRetries})", force: true);
                    options.BeforeRetry?.Invoke(ex);
                    Thread.Sleep(500 * (attempt + 1));
                }
            }
        }

        private void BeginPhase(FlashPhase phase, long phaseTotal)
        {
            _phase = phase;
            PhaseTotal = phaseTotal;
            _samples.Clear();
            _speed = 0;
            _phaseWatch.Restart();
            Report(phase, 0, null, force: true);
        }

        public void Report(FlashPhase phase, long done, string? message, bool force = false)
        {
            LastDone = done;
            if (progress is null) return;
            long now = Stopwatch.GetTimestamp();
            if (!force && Stopwatch.GetElapsedTime(_lastReportTicks, now).TotalMilliseconds < 150 && done < PhaseTotal) return;
            _lastReportTicks = now;

            double t = _phaseWatch.Elapsed.TotalSeconds;
            if (phase is FlashPhase.Writing or FlashPhase.Verifying)
            {
                _samples.Enqueue((t, done));
                while (_samples.Count > 2 && t - _samples.Peek().T > 4) _samples.Dequeue();
                var first = _samples.Peek();
                if (t - first.T > 0.3) _speed = (done - first.Done) / (t - first.T);
                else if (t > 0) _speed = done / t;
            }

            TimeSpan? remaining = null;
            if (_speed > 1 && phase is FlashPhase.Writing or FlashPhase.Verifying)
                remaining = TimeSpan.FromSeconds((PhaseTotal - done) / _speed);

            progress.Report(new FlashProgress(phase, done, PhaseTotal, BytesWritten, BytesSkipped,
                phase is FlashPhase.Writing or FlashPhase.Verifying ? _speed : 0,
                _phaseWatch.Elapsed, remaining, message));
        }

        public FlashResult Result(TimeSpan elapsed, bool success) => new()
        {
            Success = success,
            TotalBytes = _total,
            BytesWritten = BytesWritten,
            BytesSkipped = BytesSkipped,
            BytesVerified = BytesVerified,
            BytesProcessed = BytesWritten + BytesSkipped,
            WriteDuration = WriteDuration == TimeSpan.Zero && _phase == FlashPhase.Writing ? _phaseWatch.Elapsed : WriteDuration,
            VerifyDuration = VerifyDuration,
            TotalDuration = elapsed,
        };
    }

    /// <summary>
    /// Decides whether to read-compare the next chunk. After 4 differing chunks in a row it pauses
    /// comparing for a back-off window (16 → 256 chunks), then probes one chunk again.
    /// </summary>
    internal sealed class AdaptiveCompare(bool enabled)
    {
        private int _diffStreak, _pause, _backoff = 16;

        public bool ShouldCompare()
        {
            if (!enabled) return false;
            if (_pause > 0) { _pause--; return false; }
            return true;
        }

        public void Record(bool same)
        {
            if (same)
            {
                _diffStreak = 0;
                _backoff = 16;
                return;
            }
            if (++_diffStreak >= 4)
            {
                _pause = _backoff;
                _backoff = Math.Min(_backoff * 2, 256);
            }
        }
    }

    /// <summary>An I/O error on the source side (image file, or the card when backing up).</summary>
    private sealed class SourceReadException(DeviceIoException inner) : Exception(inner.Message, inner)
    {
        public DeviceIoException Inner { get; } = inner;
    }

    /// <summary>Background reader that keeps a few source chunks ready in aligned buffers.</summary>
    private sealed class ChunkReader : IDisposable
    {
        private readonly BlockingCollection<AlignedBuffer> _free;
        private readonly BlockingCollection<(int Index, AlignedBuffer Buffer, int Length)> _filled;
        private readonly List<AlignedBuffer> _all = [];
        private readonly CancellationTokenSource _cts;
        private readonly Thread _thread;
        private Exception? _error;

        public ChunkReader(IBlockDevice source, long sourceLength, IReadOnlyList<int> order, Func<int, long> offsetOf,
            Func<int, int> lengthOf, int chunkSize, int buffers, CancellationToken ct)
        {
            buffers = Math.Max(2, buffers);
            _free = new BlockingCollection<AlignedBuffer>(buffers);
            _filled = new BlockingCollection<(int, AlignedBuffer, int)>(buffers);
            for (int i = 0; i < buffers; i++)
            {
                var b = new AlignedBuffer(chunkSize);
                _all.Add(b);
                _free.Add(b);
            }
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = _cts.Token;
            int sourceAlign = Math.Max(1, source.SectorSize);

            _thread = new Thread(() =>
            {
                try
                {
                    foreach (int index in order)
                    {
                        var buf = _free.Take(token);
                        long offset = offsetOf(index);
                        int len = lengthOf(index);
                        long available = Math.Clamp(sourceLength - offset, 0, len);
                        // Physical-disk sources need sector-aligned reads; the tail is zero-filled anyway.
                        int readLen = (int)((available + sourceAlign - 1) / sourceAlign * sourceAlign);
                        readLen = (int)Math.Min(readLen, Math.Max(0, source.Length - offset));
                        if (readLen > 0) source.Read(buf.Slice(0, readLen), offset);
                        if (available < len) buf.Slice((int)available, len - (int)available).Clear();
                        _filled.Add((index, buf, len), token);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _error = ex; }
                finally { _filled.CompleteAdding(); }
            })
            { IsBackground = true, Name = "ScaleFlash reader" };
            _thread.Start();
        }

        public IEnumerable<(int Index, AlignedBuffer Buffer, int Length)> Consume()
        {
            foreach (var item in _filled.GetConsumingEnumerable(_cts.Token))
                yield return item;
            if (_error is DeviceIoException io) throw new SourceReadException(io);
            if (_error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(_error);
            _cts.Token.ThrowIfCancellationRequested();
        }

        public void Release(AlignedBuffer buffer) => _free.Add(buffer);

        public void Dispose()
        {
            _cts.Cancel();
            _thread.Join();
            foreach (var b in _all) b.Dispose();
            _free.Dispose();
            _filled.Dispose();
            _cts.Dispose();
        }
    }
}
