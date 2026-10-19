# VibeMiner artwork

`vibeminer.svg` is the original Excavate vector artwork carried forward from the Java project. Its source revision and digest are recorded in [the reference manifest](../docs/migration/java-reference.json). It is an original GPL-3.0-or-later project material.

The browser mockups and React header resolve this SVG. `icons/vibeminer.png` (512×512 RGBA), `vibeminer.ico` (Windows multi-size) and `vibeminer.icns` (macOS through 1024px) are unchanged original Java exports from the same recorded checkpoint, with their own hashes in the manifest. No new logo was generated.

The desktop build copies the PNG into its application assets for the window/taskbar icon; macOS development also sets its dock icon. Packaging selects the original ICO/ICNS on their native platforms. Linux uses the BrowserWindow PNG because ELF executables do not embed an application icon through Electron Packager. Windows/macOS native appearance remains unqualified until those platforms are exercised.

Keep derived variants traceable to the canonical SVG and avoid editing their pixels independently. Java integration is provenance, not evidence of Electron's native-platform behavior.
