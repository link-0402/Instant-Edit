# XIV Instant Edit

XIV Instant Edit is a focused Blender extension for the Instant Edit Dalamud
plugin. It contains only the model I/O and scene tools needed for the bridge,
plus a standalone Simple Export panel.

## Features

- Receives secure, versioned `.mdl` import requests from Instant Edit.
- Creates isolated import collections and preserves the authorized Penumbra
  export destination.
- Supports generated import armatures or a named existing scene armature.
- Displays and assigns the FFXIV material path for every visible mesh group.
- Supports adding new visible mesh parts and groups anywhere in the scene using
  YAA-compatible `group.part Name` object names.
- Quick Export back to the original source mod, including variants and optional
  Penumbra setup.
- Persists import authorization in the Dalamud plugin and reconnects saved scene
  contexts after Blender or plugin restarts.
- Penumbra-compatible SELF, ALL, and Glamourer-assisted redraw modes after
  Quick Export.
- Simple Export to MDL, FBX, or glTF for visible mesh objects.
- Export-time UV2 copy/clear, vertex color/alpha cleanup, and flow-data cleanup.

## Installation

Build or install the extension ZIP through Blender's Extensions preferences.
The source directory itself can also be used for development.

Do not enable this extension at the same time as a custom Yet Another Addon
build that also contains the Instant Edit listener. Both would attempt to own
the same local port (42424 by default). The unmodified upstream Yet Another
Addon can coexist because it does not provide that listener.

The connection ports can be changed in the extension preferences. Simple
Export and Instant Edit controls are in the **XIV Instant Edit** sidebar tab of
the 3D Viewport.

## Attribution and license

This extension is derived from
[Yet Another Addon](https://github.com/Arrenval/Yet-Another-Addon) by Arrenval
and vendors the relevant portions of
[XIVPy](https://github.com/Arrenval/XIVPy).

It is distributed under the GNU General Public License version 3 or later.
See `LICENSE`, `xivpy/LICENSE`, `THIRD_PARTY_NOTICES.md`, and
`MODIFICATIONS.md`.
