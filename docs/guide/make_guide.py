#!/usr/bin/env python3
"""Packs the user guide into one HTML file for a release, with its fonts and pictures inside it.

    python3 docs/guide/make_guide.py [output.html]

The output is out/Strands_User_Guide.html unless another path is given. The screenshots are made JPEG and
1600 pixels wide, and the icon 168 pixels, by macOS's sips, so this runs on a Mac.
"""
import base64
import os
import re
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
SOURCE = os.path.join(HERE, "Strands_User_Guide.html")
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))


def data_uri(path, scratch):
    """The file at path, relative to the guide, as a data URI."""
    full = os.path.normpath(os.path.join(HERE, path))
    name = os.path.basename(full)
    if name.endswith(".ttf"):
        return "data:font/ttf;base64," + encoded(full)
    if name == "AppIcon.png":
        small = os.path.join(scratch, name)
        subprocess.run(["sips", "-Z", "168", full, "--out", small], check=True, capture_output=True)
        return "data:image/png;base64," + encoded(small)
    if name.endswith(".png"):
        jpeg = os.path.join(scratch, name[:-4] + ".jpg")
        subprocess.run(["sips", "-s", "format", "jpeg", "-s", "formatOptions", "85", "--resampleWidth", "1600",
                        full, "--out", jpeg], check=True, capture_output=True)
        return "data:image/jpeg;base64," + encoded(jpeg)
    raise SystemExit("Don't know how to pack " + path)


def encoded(path):
    with open(path, "rb") as f:
        return base64.b64encode(f.read()).decode("ascii")


def main():
    output = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, "out", "Strands_User_Guide.html")
    with open(SOURCE, encoding="utf-8") as f:
        html = f.read()
    with tempfile.TemporaryDirectory() as scratch:
        html = re.sub(r'src="(\.\./[^"]+)"', lambda m: 'src="' + data_uri(m.group(1), scratch) + '"', html)
        html = re.sub(r'url\("(\.\./[^"]+)"\)', lambda m: 'url("' + data_uri(m.group(1), scratch) + '")', html)
    # The note about this file being the source does not belong in the packed copy.
    html = re.sub(r"<!--\n  The source of the user guide.*?-->\n", "", html, flags=re.S)
    if "../" in html:
        raise SystemExit("A link was left unpacked: " + html[html.index("../") - 40:html.index("../") + 60])
    os.makedirs(os.path.dirname(os.path.abspath(output)), exist_ok=True)
    with open(output, "w", encoding="utf-8") as f:
        f.write(html)
    print("%s, %.0f KB" % (output, os.path.getsize(output) / 1024))


if __name__ == "__main__":
    main()
