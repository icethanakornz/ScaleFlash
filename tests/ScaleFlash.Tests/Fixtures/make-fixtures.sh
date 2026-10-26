#!/bin/bash
# Builds the ext2/3/4 test images and their ground-truth manifests.
# Run as root inside a Linux (WSL) environment:  bash make-fixtures.sh <output-dir>
# The manifests are produced by the Linux kernel itself (mount + find + sha256sum);
# for the "dirty" images the kernel replays the journal on a scratch copy first.
set -euo pipefail
OUT="${1:?output dir}"
WORK=$(mktemp -d)
MNT="$WORK/mnt"
mkdir -p "$MNT" "$OUT"
sysctl -qw vm.dirty_expire_centisecs=60000 vm.dirty_writeback_centisecs=60000 || true

manifest() {   # manifest <mountpoint>  -> stdout
  (cd "$1" && find . -mindepth 1 | LC_ALL=C sort | while IFS= read -r p; do
    q="${p#.}"
    if [ -L "$p" ]; then printf 'l\t%s\t%s\t%s\n' "$q" "$(stat -c %s "$p")" "$(readlink "$p")"
    elif [ -d "$p" ]; then printf 'd\t%s\t0\t-\n' "$q"
    elif [ -f "$p" ]; then printf 'f\t%s\t%s\t%s\n' "$q" "$(stat -c %s "$p")" "$(sha256sum "$p" | cut -d' ' -f1)"
    else printf 'o\t%s\t0\t-\n' "$q"; fi
  done)
}

text() { seq "$1" "$2" | sed 's/$/ scaleflash test line/'; }

common_files() {   # common_files <mountpoint>
  local m="$1"
  echo "hello world" > "$m/hello.txt"
  : > "$m/empty"
  mkdir -p "$m/emptydir" "$m/dir/sub/deep" "$m/thai" "$m/many"
  echo "deep file" > "$m/dir/sub/deep/file.txt"
  printf 'สวัสดีครับ เครื่องชั่ง\n' > "$m/thai/ไฟล์ภาษาไทย.txt"
  echo "spaces" > "$m/name with spaces.txt"
  echo "long" > "$m/$(printf 'L%.0s' $(seq 1 250)).txt"
  ln -s hello.txt "$m/link_fast"
  ln -s /this/is/a/very/long/symlink/target/that/does/not/fit/in/the/inode/at/all.txt "$m/link_slow"
  ln "$m/hello.txt" "$m/hardlink"
  for i in $(seq 1 600); do echo "file $i" > "$m/many/entry_number_$i.txt"; done
  text 1 40000 > "$m/indirect.bin"                  # ~1 MB
}

make_clean() {   # make_clean <name> <size-MB> <mkfs args...>
  local name=$1 size=$2; shift 2
  local img="$WORK/$name.img"
  truncate -s "${size}M" "$img"
  "$@" -q -F "$img"
  mount -o loop "$img" "$MNT"
  common_files "$MNT"
  case $name in
    ext2_1k)
      truncate -s 70M "$MNT/sparse_triple.bin"
      text 1 100 | dd of="$MNT/sparse_triple.bin" bs=1024 seek=1024 conv=notrunc status=none
      text 1 100 | dd of="$MNT/sparse_triple.bin" bs=1024 seek=$((66*1024)) conv=notrunc status=none ;;
    ext3_4k)
      text 1 300000 > "$MNT/big.bin"                # ~8 MB, double indirect
      truncate -s 5G "$MNT/sparse_triple.bin"
      text 1 100 | dd of="$MNT/sparse_triple.bin" bs=4096 seek=$((4500*256)) conv=notrunc status=none ;;
    ext4)
      text 1 300000 > "$MNT/big.bin"
      : > "$MNT/frag.bin"
      for i in $(seq 0 11); do text $((i*100)) $((i*100+99)) | dd of="$MNT/frag.bin" bs=4096 seek=$((i*64)) conv=notrunc status=none; done
      fallocate -l 1M "$MNT/prealloc.bin"
      for i in $(seq 1 1500); do echo "x" > "$MNT/many/htree_filler_with_a_longer_name_$i"; done ;;
  esac
  sync
  manifest "$MNT" > "$OUT/$name.manifest"
  umount "$MNT"
  gzip -9 -c "$img" > "$OUT/$name.img.gz"
}

make_dirty() {   # make_dirty <name> <mkfs args...>   -- image copied while mounted, journal not checkpointed
  local name=$1; shift
  local img="$WORK/$name.img"
  truncate -s 48M "$img"
  "$@" -q -F "$img"
  mount -o loop,commit=1 "$img" "$MNT"
  common_files "$MNT"
  sync; sleep 2
  # changes that live only in the journal when the copy is taken
  for i in $(seq 1 30); do echo "new file $i" > "$MNT/new_$i.txt"; done
  mkdir "$MNT/newdir"; text 1 20000 > "$MNT/newdir/data.bin"
  rm -f "$MNT/many/entry_number_1"*.txt
  mv "$MNT/hello.txt" "$MNT/renamed_hello.txt"
  echo "appended" >> "$MNT/dir/sub/deep/file.txt"
  sleep 3                                    # let jbd2 commit (commit=1) but not checkpoint
  cp "$img" "$WORK/$name.copy.img"
  umount "$MNT"
  # ground truth: kernel replays the journal on a scratch copy
  cp "$WORK/$name.copy.img" "$WORK/replay.img"
  mount -o loop "$WORK/replay.img" "$MNT"; manifest "$MNT" > "$OUT/$name.manifest"; umount "$MNT"
  # without replay (should differ, proving the journal matters)
  cp "$WORK/$name.copy.img" "$WORK/noload.img"
  mount -o loop,ro,noload "$WORK/noload.img" "$MNT"; manifest "$MNT" > "$OUT/$name.noreplay.manifest"; umount "$MNT"
  dumpe2fs -h "$WORK/$name.copy.img" 2>/dev/null | grep -E "features|Journal start|Journal sequence" > "$OUT/$name.info" || true
  gzip -9 -c "$WORK/$name.copy.img" > "$OUT/$name.img.gz"
}

make_clean ext2_1k 24 mkfs.ext2 -b 1024 -L ext2test
make_clean ext3_4k 64 mkfs.ext3 -b 4096 -L ext3test
make_clean ext4    64 mkfs.ext4 -L ext4test -O 64bit,metadata_csum,huge_file,flex_bg
make_dirty ext3_dirty mkfs.ext3 -b 4096 -L ext3dirty
make_dirty ext4_dirty mkfs.ext4 -L ext4dirty
rm -rf "$WORK"
ls -la "$OUT"
