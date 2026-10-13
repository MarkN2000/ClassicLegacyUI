"""投稿せず、ワークフロー内のZIP検査を実行する。Python標準ライブラリのみ使用。"""

import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import textwrap
import zipfile

workflow = (Path(__file__).resolve().parents[1] / ".github/workflows/publish-thunderstore.yml").read_text(encoding="utf-8")
code = textwrap.dedent(workflow.split("python3 - <<'PY'\n", 1)[1].split("          PY\n", 1)[0])
with tempfile.TemporaryDirectory() as temporary:
    root = Path(temporary)
    for case in ("valid", "wrong-name", "wrong-version", "extra-file", "duplicate-entry", "multiple-zips", "missing-zip"):
        directory = root / case
        (directory / "package").mkdir(parents=True)
        package = directory / "package/ClassicLegacyUI-test.zip"
        manifest = {"name": "Other" if case == "wrong-name" else "ClassicLegacyUI",
                    "version_number": "invalid" if case == "wrong-version" else "0.2.3"}
        if case != "missing-zip":
            with zipfile.ZipFile(package, "w") as archive:
                archive.writestr("manifest.json", json.dumps(manifest))
                for name in ("README.md", "LICENSE", "icon.png", "plugins/ClassicLegacyUI.dll"):
                    archive.writestr(name, b"test")
                if case == "extra-file":
                    archive.writestr("unwanted.dll", b"test")
                if case == "duplicate-entry":
                    archive.writestr("README.md", b"duplicate")
        if case == "multiple-zips":
            (directory / "package/ClassicLegacyUI-other.zip").write_bytes(package.read_bytes())
        result = subprocess.run([sys.executable, "-c", code], cwd=directory, capture_output=True, text=True,
                                env={**os.environ, "GITHUB_OUTPUT": str(directory / "output")})
        assert (result.returncode == 0) == (case == "valid"), f"{case}: {result.stdout}{result.stderr}"
        if case == "valid":
            assert (directory / "output").read_text().strip() == "version=0.2.3"
            assert (directory / "package.zip").is_file()
print("PASS: Release package validation; no upload performed.")
