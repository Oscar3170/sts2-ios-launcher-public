"""Prepare the host project from pinned dependencies. No game file takes part.

The host project holds only what the host itself owns: its launcher scene, the native extension registrations, the static
SteamKit2 and the launcher icon. The game's files reach the phone's data folder instead.
"""

import argparse
import json
import shutil
import subprocess
from pathlib import Path

import patch_fmod_file_thread
from hashing import sha256_file

ROOT = Path(__file__).resolve().parents[1]
HOST = ROOT / "src/STS2Mobile.FullGame"
ICON_SOURCE = HOST / "branding/AppIcon.png"

FMOD_EXTENSION = """[configuration]
entry_symbol="fmod_library_init"
compatibility_minimum="4.5"
[libraries]
macos.editor="res://addons/fmod/libs/macos/libGodotFmod.macos.template_release.framework"
macos.debug="res://addons/fmod/libs/macos/libGodotFmod.macos.template_release.framework"
macos.release="res://addons/fmod/libs/macos/libGodotFmod.macos.template_release.framework"
ios.debug="res://addons/fmod/libs/ios/libGodotFmod.ios.template_debug.xcframework"
ios.release="res://addons/fmod/libs/ios/libGodotFmod.ios.template_release.xcframework"
[dependencies]
macos.editor={"libs/macos/libfmod.dylib":"", "libs/macos/libfmodstudio.dylib":""}
macos.debug={"libs/macos/libfmod.dylib":"", "libs/macos/libfmodstudio.dylib":""}
macos.release={"libs/macos/libfmod.dylib":"", "libs/macos/libfmodstudio.dylib":""}
ios.debug={"libs/ios/libfmodL_iphoneos.a":"", "libs/ios/libfmodstudioL_iphoneos.a":""}
ios.release={"libs/ios/libfmod_iphoneos.a":"", "libs/ios/libfmodstudio_iphoneos.a":""}
"""


def verify_native(directory: Path, expected: str) -> None:
    manifest = json.loads((directory / "manifest.json").read_text())
    if manifest.get("version", manifest.get("spine_series")) != expected:
        raise ValueError("Unsupported native dependency version")
    for entry in manifest["files"]:
        if sha256_file(directory / entry["path"]) != entry["sha256"]:
            raise ValueError(f"Native dependency hash mismatch: {entry['path']}")


def stage_native_extensions() -> list[dict]:
    """Rebuild the host's own addons directory from the pinned vendor inputs."""
    addons = HOST / "addons"
    if addons.is_symlink():
        raise ValueError("Host addons must not link into an asset staging tree")
    if addons.exists():
        shutil.rmtree(addons)
    changes = []
    libs = addons / "fmod/libs"
    shutil.copytree(ROOT / "vendor/fmod/2.03.06/gdextension/ios", libs / "ios")
    # Only the release library ships (ios.release); the debug one is left as it came.
    for library in sorted((libs / "ios").rglob(patch_fmod_file_thread.LIBRARY)):
        changes.append({"path": str(library.relative_to(HOST)), **patch_fmod_file_thread.patch_file(library)})
    for library in sorted((libs / "ios").rglob("*.dylib")):
        original = sha256_file(library)
        subprocess.run(["install_name_tool", "-id", f"@rpath/{library.name}", str(library)], check=True)
        changes.append(
            {
                "path": str(library.relative_to(HOST)),
                "inputSha256": original,
                "outputSha256": sha256_file(library),
                "change": "Normalize device FMOD install name for app Frameworks packaging.",
            }
        )
    for name in ("libfmod_iphoneos.a", "libfmodstudio_iphoneos.a", "libfmodL_iphoneos.a", "libfmodstudioL_iphoneos.a"):
        shutil.copy2(ROOT / "vendor/fmod/2.03.06/lib/device" / name, libs / "ios" / name)
    # The editor that performs the export loads the macOS wrapper. It is not shipped.
    shutil.copytree(ROOT / "vendor/fmod/2.03.06/gdextension/macos", libs / "macos", symlinks=True)
    (addons / "fmod/fmod.gdextension").write_text(FMOD_EXTENSION)
    spine_source = ROOT / "vendor/spine/4.2-godot4.5.1/bin"
    spine = addons / "spine"
    for platform in ("ios", "macos"):
        shutil.copytree(spine_source / platform, spine / platform, symlinks=True)
    shutil.copy2(spine_source / "spine_godot_extension.gdextension", spine / "spine_godot_extension.gdextension")
    return changes


def install_icon(source: Path = ICON_SOURCE, destination: Path = HOST / "icon.png") -> None:
    """Put the launcher's icon where Godot imports it and the Xcode export reads it."""
    try:
        data = source.read_bytes()
    except FileNotFoundError:
        raise ValueError(f"Missing launcher icon: {source}") from None
    destination.write_bytes(data)


def prepare(steam: Path) -> None:
    verify_native(ROOT / "vendor/fmod/2.03.06", "2.03.06")
    verify_native(ROOT / "vendor/spine/4.2-godot4.5.1", "4.2")
    native_changes = stage_native_extensions()
    install_icon()
    staged = HOST / "staged"
    staged.mkdir(exist_ok=True)
    steamkit = {}
    for target in ("net9.0", "net10.0"):
        destination = staged / target
        destination.mkdir(exist_ok=True)
        shutil.copy2(steam / target / "SteamKit2.dll", destination / "SteamKit2.dll")
        steamkit[target] = sha256_file(destination / "SteamKit2.dll")
    (staged / "host-preparation.json").write_text(json.dumps({"steamKit": steamkit, "nativeChanges": native_changes}, indent=2) + "\n")
    print("Prepared the host project.")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--steam", type=Path, required=True, help="folder of SteamKit2.dll per target framework (scripts/stage-steam.sh)")
    args = parser.parse_args()
    try:
        prepare(args.steam)
    except ValueError as error:
        raise SystemExit(str(error)) from None


if __name__ == "__main__":
    main()
