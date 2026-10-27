"""Write THIRD-PARTY-NOTICES.txt from what the Release build actually ships.

Run after a restore or publish of src/Quarterdeck.App (it reads obj/project.assets.json and the
NuGet package cache), then commit the result:  python3 helpers/third_party_notices.py

Packages come from the win-x64 target, keeping only those with runtime or native files. Licence
texts come from each package's own licence file when it has one, else from its SPDX id. The
components that are not NuGet packages (xterm.js, noVNC, LibVLC's files, the .NET runtime) are
described in MANUAL below; keep that list in step with what ships.
"""
import glob
import hashlib
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS = os.path.join(ROOT, "src/Quarterdeck.App/obj/project.assets.json")
# The app's own target framework, so raising the Windows SDK version cannot leave this behind.
TARGET = re.search(r"<TargetFramework>([^<]+)</TargetFramework>",
                   open(os.path.join(ROOT, "src/Quarterdeck.App/Quarterdeck.App.csproj")).read()).group(1) + "/win-x64"
NUGET = os.environ.get("NUGET_PACKAGES", os.path.expanduser("~/.nuget/packages"))
LICENSES = "/usr/share/common-licenses"
OUT = os.path.join(ROOT, "THIRD-PARTY-NOTICES.txt")

MIT = """Permission is hereby granted, free of charge, to any person obtaining a copy of this software
and associated documentation files (the "Software"), to deal in the Software without
restriction, including without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the
Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING
BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE."""

BSD3 = """Redistribution and use in source and binary forms, with or without modification, are permitted
provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this list of
   conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice, this list of
   conditions and the following disclaimer in the documentation and/or other materials provided
   with the distribution.
3. Neither the name of the copyright holder nor the names of its contributors may be used to
   endorse or promote products derived from this software without specific prior written
   permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR
IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND
FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR
CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER
IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT
OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE."""

# Full texts printed once at the end, keyed by SPDX id. The GNU and Mozilla texts come from the
# Debian common-licenses folder; load_texts() reads them when main() runs, with a plain message
# on a host that has no such folder.
TEXTS = {"MIT": MIT, "BSD-3-Clause": BSD3}
SYSTEM_TEXTS = (("Apache-2.0", "Apache-2.0"), ("LGPL-2.1-or-later", "LGPL-2.1"),
                ("LGPL-3.0-or-later", "LGPL-3"), ("GPL-3.0", "GPL-3"), ("MPL-2.0", "MPL-2.0"))


def load_texts():
    for spdx, fname in SYSTEM_TEXTS:
        path = os.path.join(LICENSES, fname)
        if not os.path.isfile(path):
            sys.exit(f"{path} not found: this script needs the Debian/Ubuntu common-licenses folder")
        TEXTS[spdx] = open(path, encoding="utf-8").read().strip()

# Not NuGet packages, or a package whose files the build picks by hand.
MANUAL = [
    ("xterm.js 6.0.0, with its addons fit 0.11.0, search 0.16.0, serialize 0.14.0 and web-links "
     "0.12.0 (Assets), unmodified",
     "Copyright (c) 2017-2019, The xterm.js authors (https://github.com/xtermjs/xterm.js)\n"
     "Copyright (c) 2014-2016, SourceLair Private Company (https://www.sourcelair.com)\n"
     "Copyright (c) 2012-2013, Christopher Jeffrey (https://github.com/chjj/)",
     "MIT", None),
    ("noVNC 1.7.0 core library (Assets/novnc), unmodified",
     "Copyright (C) 2022 The noVNC authors. https://github.com/novnc/noVNC\n"
     "Its own LICENSE.txt ships beside it in Assets/novnc. core/crypto/des.js carries the notices of\n"
     "Widget Workshop, Inc. (1996, Dave Zimmerman), AT&T Laboratories Cambridge (1999) and Jef Poskanzer "
     "(1996, BSD-style: Copyright (C) 1996 by Jef Poskanzer <jef@acme.com>. All rights reserved.) in the "
     "file, which was ported from the Flashlight VNC ActionScript implementation.",
     "MPL-2.0", None),
    ("pako, bundled inside noVNC (Assets/novnc/vendor/pako)",
     None, None, "src/Quarterdeck.App/Assets/novnc/vendor/pako/LICENSE"),
    ("LibVLC 3.0.24 (VideoLAN.LibVLC.Windows): libvlc.dll, libvlccore.dll and the plugins under "
     "libvlc\\win-x64, unmodified separate DLLs",
     "Copyright (C) the VideoLAN team and VLC authors. https://www.videolan.org\n"
     "Source: vlc-3.0.24.tar.xz is attached to every BaudHoundTerm release on GitHub, next to the "
     "zip (sha256 e7cab503d1d7d5849b89d2cf0e1ee60d0ef6d012407791b644b9cfc0cc225fdf), and is also at "
     "https://download.videolan.org/pub/videolan/vlc/3.0.24/vlc-3.0.24.tar.xz. The DLLs are VideoLAN's "
     "own 3.0.24 win64 build; the source of the libraries built into them (FFmpeg, LIVE555 and the "
     "others named below) is in VLC's contrib set for that release. Written offer: for three years "
     "from the release date, MAH7 (info@mah7.com) will supply the complete corresponding source of "
     "every LGPL component shipped in this build to anyone who asks, for no more than the cost of "
     "sending it.\n"
     "Every shipped VLC file is LGPL-2.1-or-later; the two GPL-only audio filters are left out of "
     "the build. Under the LGPL you may replace these DLLs with your own build of the same LibVLC "
     "version.\n"
     "libavcodec_plugin.dll contains FFmpeg (https://ffmpeg.org), built as LGPL-2.1-or-later.\n"
     "liblive555_plugin.dll contains LIVE555 Streaming Media (http://www.live555.com), GNU LGPL; "
     "the LGPL-3.0 text and the GPL-3.0 it builds on are included below in case the bundled "
     "release is version 3.\n"
     "Also statically linked inside these DLLs: libgcrypt and libgpg-error (LGPL-2.1-or-later, "
     "Copyright g10 Code GmbH and the Free Software Foundation), libdvbpsi (LGPL-2.1-or-later, "
     "VideoLAN), libpng 1.6.58 (PNG Reference Library License v2; Copyright (c) 1995-1996 Guy Eric "
     "Schalnat, Group 42, Inc., 1996-1997 Andreas Dilger, 1998-2018 Glenn Randers-Pehrson, "
     "2018-2026 Cosmin Truta), zlib 1.3.2 (zlib licence, Copyright 1995-2026 Jean-loup Gailly and "
     "Mark Adler), speexdsp (BSD-3-Clause, Copyright 2002-2008 Xiph.Org Foundation and "
     "Jean-Marc Valin) and libmysofa (BSD-3-Clause, Copyright 2016-2026 Christian Hoene, "
     "Symonics GmbH). The BSD-3-Clause text is below.",
     "LGPL-2.1-or-later", None),
    ("Agent skill network-device.md (built into the executable) draws on \"parsing-fortinet-configs\" "
     "from fwskillsshare; the shipped skill is rewritten, not a copy",
     "Copyright (c) 2026 fwskillsshare contributors. https://github.com/mechubsec/fwskillsshare",
     "MIT", None),
    (".NET 10 runtime and Windows Desktop runtime (WPF, Windows Forms), bundled into "
     "BaudHoundTerm.exe by the self-contained publish",
     "Copyright (c) .NET Foundation and Contributors. https://github.com/dotnet\n"
     "The runtime's native parts in dll\\ (PresentationNative_cor3.dll, wpfgfx_cor3.dll, "
     "PenImc_cor3.dll, D3DCompiler_47_cor3.dll, vcruntime140_cor3.dll) are closed-source Microsoft "
     "components, Copyright (c) Microsoft Corporation, redistributed under the Microsoft .NET "
     "Library licence: https://dotnet.microsoft.com/dotnet_library_license.htm",
     "MIT", None),
    ("Microsoft.Windows.SDK.NET.dll and WinRT.Runtime.dll (Microsoft.Windows.SDK.NET.Ref "
     "10.0.22621.57, the Windows API projection used for Windows OCR), bundled into BaudHoundTerm.exe",
     "Copyright (c) Microsoft Corporation. All rights reserved.\n"
     "Microsoft.Windows.SDK.NET.dll: Windows SDK licence, https://aka.ms/WinSDKLicenseURL, which "
     "permits distributing it with an application.\n"
     "WinRT.Runtime.dll is C#/WinRT (https://github.com/microsoft/CsWinRT), MIT License (text below).",
     "MIT", None),
]


# Packages whose nuspec names no copyright holder, or not all of them: the line from the project's own
# LICENSE (SSH.NET), or its author where the licence file is the bare LGPL text (LibVLCSharp).
COPYRIGHT_FIX = {
    "ssh.net": "Copyright (c) Renci, Oleg Kapeljushnik, Gert Driesen and contributors",
    "libvlcsharp": "By VideoLAN and the LibVLCSharp contributors. https://code.videolan.org/videolan/LibVLCSharp",
    "libvlcsharp.wpf": "By VideoLAN and the LibVLCSharp contributors. https://code.videolan.org/videolan/LibVLCSharp",
}


def nuspec(d):
    s = open(glob.glob(d + "/*.nuspec")[0], encoding="utf-8", errors="ignore").read()
    get = lambda tag: (re.search(rf"<{tag}[^>]*>([^<]*)", s) or [None, ""])[1].strip()
    lic = re.search(r'<license type="(\w+)">([^<]+)', s)
    return get("copyright"), get("projectUrl"), lic.group(2) if lic and lic.group(1) == "expression" else None


def license_files(d):
    return sorted(f for f in os.listdir(d)
                  if re.match(r"(licen[cs]e|notice)", f, re.I) and not f.lower().endswith((".png", ".svg")))


def main():
    load_texts()
    target = json.load(open(ASSETS))["targets"][TARGET]
    pkgs = []
    for key, v in target.items():
        if v.get("type") != "package":
            continue
        files = [p for s in ("runtime", "native", "runtimeTargets") for p in v.get(s, {}) if not p.endswith("_._")]
        if files:
            name, ver = key.split("/")
            pkgs.append((name, ver, os.path.join(NUGET, name.lower(), ver)))
    pkgs.sort(key=lambda p: p[0].lower())

    out = ["BaudHoundTerm: third-party notices",
           "",
           "BaudHoundTerm is licensed under the Apache License 2.0 (LICENSE, NOTICE). It ships the",
           "third-party components below; each keeps its own licence. The application icon was made",
           "for MAH7 with Claude (Anthropic).",
           "",
           f"Generated by helpers/third_party_notices.py from the build's package list. Edit that script,",
           "not this file.",
           ""]
    seen_text, used = {}, set()

    def emit(title, copyright_, spdx, files_dir=None, files=(), extra=None):
        out.append("=" * 100)
        out.append(title)
        out.append("=" * 100)
        if copyright_:
            out.append(copyright_)
        if spdx:
            used.add(spdx)
            out.append(f"Licence: {spdx} (full text under LICENCE TEXTS at the end)")
        for f in files:
            body = open(os.path.join(files_dir, f), encoding="utf-8", errors="replace").read().strip()
            h = hashlib.sha1(body.encode()).hexdigest()
            if h in seen_text:
                out.append(f"{f}: same text as given above for {seen_text[h]}")
            else:
                seen_text[h] = title.split(" ")[0]
                out.extend(["", f"--- {f} ---", body])
        if extra:
            out.extend(["", extra])
        out.append("")

    for title, copyright_, spdx, path in MANUAL:
        if path:
            emit(title, None, None, os.path.join(ROOT, os.path.dirname(path)), [os.path.basename(path)])
        else:
            emit(title, copyright_, spdx)
    # The runtime's own notices file, once (the dotnet/runtime packages repeat it).
    rt = glob.glob(os.path.join(NUGET, "microsoft.netcore.app.runtime.win-x64", "*", "THIRD-PARTY-NOTICES.TXT"))
    if rt:
        emit(".NET runtime: third-party notices", None, None, os.path.dirname(rt[0]), ["THIRD-PARTY-NOTICES.TXT"])

    for name, ver, d in pkgs:
        if not os.path.isdir(d):
            sys.exit(f"missing package folder {d}: restore the project first")
        copyright_, url, spdx = nuspec(d)
        copyright_ = COPYRIGHT_FIX.get(name.lower(), copyright_)
        files = license_files(d)
        # An SPDX id we can print in full beats a copy of the same MIT text in every package.
        if spdx in TEXTS:
            files = [f for f in files if not re.match(r"licen[cs]e", f, re.I)]
        if not spdx and not files:
            sys.exit(f"{name} {ver}: no licence id or licence file; add it to MANUAL")
        emit(f"{name} {ver}", "\n".join(x for x in (copyright_, url) if x), spdx if spdx in TEXTS else None, d,
             files, None if (spdx in TEXTS or not spdx) else f"Licence: {spdx}")

    out += ["#" * 100, "LICENCE TEXTS", "#" * 100, ""]
    # LIVE555 inside the VLC build may be LGPL-3 (it says only "LGPL"); speexdsp and libmysofa in
    # the same DLLs are BSD-3-Clause. Neither is a package, so name their texts here.
    used |= {"LGPL-3.0-or-later", "GPL-3.0", "BSD-3-Clause"}
    for spdx in sorted(used):
        out += ["=" * 100, spdx, "=" * 100, TEXTS[spdx], ""]

    with open(OUT, "w", encoding="utf-8", newline="\r\n") as f:
        f.write("\n".join(out).rstrip() + "\n")
    print(f"{OUT}: {len(pkgs)} packages, {len(MANUAL)} other components, {os.path.getsize(OUT)} bytes")


if __name__ == "__main__":
    main()
