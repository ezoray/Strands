#!/bin/bash
# Packs the builds in Builds/ into one zip per platform for a release, each with the app, the user
# guide (packed into one file by docs/guide/make_guide.py), the licence and the third-party notices.
#
#     Tools/make_release.sh [version]
#
# The version is Version under Player in the Project Settings unless one is given. The zips go to
# out/Release/<version>/, with a SHA256SUMS.txt beside them. It runs on a Mac (ditto, sips, lipo,
# codesign) and does not build anything: make the three builds from one commit first, and note the
# commit in 0.3 of docs/Test-Plan.md.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

VERSION="${1:-$(sed -n 's/^  bundleVersion: //p' ProjectSettings/ProjectSettings.asset)}"
[ -n "$VERSION" ] || { echo "No version found in ProjectSettings.asset; give one." >&2; exit 1; }
OUT="out/Release/$VERSION"
if [ -e "$OUT" ]; then
    echo "$OUT already exists. Remove it first, or give another version." >&2
    exit 1
fi

MAC="Builds/macOS/Strands.app"
WIN="Builds/Windows"
LINUX="Builds/Linux"
[ -d "$MAC" ] || { echo "No macOS build at $MAC." >&2; exit 1; }
[ -f "$WIN/Strands.exe" ] || { echo "No Windows build in $WIN." >&2; exit 1; }
[ -f "$LINUX/Strands.x86_64" ] || { echo "No Linux build in $LINUX." >&2; exit 1; }

# The macOS app must run on both kinds of Mac, and carry its (ad hoc) signature intact.
ARCHS="$(lipo -archs "$MAC/Contents/MacOS/Strands")"
case "$ARCHS" in
    *x86_64*arm64*|*arm64*x86_64*) ;;
    *) echo "The macOS build is not universal (it is $ARCHS). Set the build profile to Intel + Apple silicon." >&2; exit 1 ;;
esac
codesign --verify --deep "$MAC" || { echo "The macOS build's signature does not verify." >&2; exit 1; }

if [ -n "$(git status --porcelain 2>/dev/null)" ]; then
    echo "Note: the working tree has changes that are not committed."
fi

STAGE="$(mktemp -d)"
trap 'rm -rf -- "$STAGE"' EXIT
mkdir -p "$OUT"

echo "Packing the user guide"
python3 docs/guide/make_guide.py "$STAGE/Strands_User_Guide.html" >/dev/null

# What goes beside the app in every zip.
extras() {
    cp "$STAGE/Strands_User_Guide.html" THIRD_PARTY_NOTICES.txt LICENSE "$1/"
}

# A zip of a folder's contents, with no __MACOSX entries and no Mac-only attributes.
zip_contents() {
    ditto -c -k --norsrc --noextattr --noacl "$1" "$2"
}

# A Windows or Linux build in a Strands folder, leaving out what Unity says not to ship
# (Burst's debug information and the folder it says to back up but not to ship).
copy_player() {
    mkdir -p "$2/Strands"
    rsync -a --exclude '.DS_Store' --exclude '*_BackUpThisFolder_ButDontShipItWithYourGame' \
        --exclude '*_BurstDebugInformation_DoNotShip' "$1/" "$2/Strands/"
}

echo "macOS"
mkdir -p "$STAGE/mac"
ditto --norsrc --noextattr --noacl "$MAC" "$STAGE/mac/Strands.app"
extras "$STAGE/mac"
zip_contents "$STAGE/mac" "$OUT/Strands-$VERSION-macOS-Universal.zip"

echo "Windows"
copy_player "$WIN" "$STAGE/win"
extras "$STAGE/win"
zip_contents "$STAGE/win" "$OUT/Strands-$VERSION-Windows-x64.zip"

echo "Linux (untested)"
copy_player "$LINUX" "$STAGE/linux"
extras "$STAGE/linux"
# Nothing has ever been run on Linux, and the name says so until it has.
zip_contents "$STAGE/linux" "$OUT/Strands-$VERSION-Linux-x64-untested.zip"

(cd "$OUT" && shasum -a 256 Strands-*.zip > SHA256SUMS.txt)

echo
# A build's date is its compiled code's, which every build writes again: Unity leaves the executables
# and its settings files alone when they have not changed.
echo "Builds packed (each build's date):"
echo "  macOS    $(date -r "$MAC/Contents/Resources/Data/Managed/Assembly-CSharp.dll" '+%Y-%m-%d %H:%M')"
echo "  Windows  $(date -r "$WIN/Strands_Data/Managed/Assembly-CSharp.dll" '+%Y-%m-%d %H:%M')"
echo "  Linux    $(date -r "$LINUX/Strands_Data/Managed/Assembly-CSharp.dll" '+%Y-%m-%d %H:%M')"
echo "Commit checked out: $(git rev-parse --short HEAD 2>/dev/null || echo none)"
echo
ls -lh "$OUT"
