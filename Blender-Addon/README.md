# XIV Instant Edit

XIV Instant Edit is a focused Blender extension for the XIV Instant Edit Dalamud
plugin. It contains only the model I/O and scene tools needed for the bridge,
plus a standalone Simple Import/Export panel.

## Features

- Receives secure, versioned `.mdl` import requests from XIV Instant Edit.
- Creates isolated import collections and preserves the authorized Penumbra
  export destination.
- Supports generated import armatures or a named existing scene armature.
- Displays and assigns the FFXIV material path for every visible mesh group.
- Highlights only the mesh-part rows whose normalized export material is
  missing or differs from the first part used by their mesh group.
- Optionally builds import-local, packed Principled BSDF previews from the
  effective game/Penumbra MTRL and TEX resources supplied by the plugin.
- Supports adding new visible mesh parts and groups anywhere in the scene using
  YAA-compatible `group.part Name` object names.
- Includes a Toolbox action to convert suffix-form mesh IDs from Textools/FBX
  scenes into the prefix naming convention.
- Quick Export back to the original source mod, including variants and optional
  Penumbra setup.
- Saves a single-context export as a self-contained new Penumbra mod with its
  captured material and texture dependencies.
- Creates self-contained multi-context mashups as a new group in the active mod
  or as a new Penumbra mod, with isolated material and texture dependencies.
- Automatically shifts incoming MDL mesh groups away from visible conflicts
  while preserving incoming part numbers and labels.
- Persists import authorization in the Dalamud plugin and reconnects saved scene
  contexts after Blender or plugin restarts.
- Recovers export receipts after network timeouts without submitting a second
  write, and durably queues context revocations while the plugin is offline.
- Redraws the local player and their currently spawned summons, minions, and
  mounts after Quick Export.
- Simple Export to MDL, FBX, or glTF for visible mesh objects, with an explicit
  `All except...` mesh exclusion option.
- Simple Import from MDL or FBX files.
- Simple Import/Export settings are separated into dedicated option sections,
  including the import-time export-folder setting.
- Export-time UV2 copy/clear, vertex color/alpha cleanup, and flow-data cleanup.
- Optional **Reset Scaling on Export** neutralization; armature rest-pose
  neutralization and complete state restoration remain automatic.

Meshes in one export may resolve to different Blender armatures. Each mesh uses
its parent armature when present, otherwise its first valid Armature modifier.
Every resolved rig is evaluated in rest pose. The resulting FFXIV MDL merges the
used bone names into one bone list; it does not preserve separate Blender
armature identities.

Material previews are display-only and intentionally approximate. They do not
include actor colors or dye baking, do not add material/texture editing, and
are never included in Quick Export. Missing preview resources fall back to the
existing colored placeholder without blocking geometry import. Gear using
`character.shpk`-family colorsets and index textures is composed into a packed
base-color preview. Standalone
Simple Import does not resolve FFXIV resources and is unchanged.

When the Dalamud **Exclude body and general materials** sub-option is enabled,
body skin, body-piercing, and pube slots intentionally retain their colored
placeholders without producing missing-preview warnings.

## Installation

Build or install the extension ZIP through Blender's Extensions preferences.
The source directory itself can also be used for development with Blender 4.5.3
or newer.

Do not enable this extension at the same time as a custom Yet Another Addon
build that also contains the XIV Instant Edit listener. Both would attempt to own
the same local port (42424 by default). The unmodified upstream Yet Another
Addon can coexist because it does not provide that listener.

The connection ports can be changed in the extension preferences. Simple
Export and XIV Instant Edit controls are in the **XIV Instant Edit** sidebar tab of
the 3D Viewport.

## Attribution and license

This extension is derived from
[Yet Another Addon](https://github.com/Arrenval/Yet-Another-Addon) by Arrenval
and vendors the relevant portions of
[XIVPy](https://github.com/Arrenval/XIVPy).

It is distributed under the GNU General Public License version 3 or later.
See `LICENSE`, `xivpy/LICENSE`, and `THIRD_PARTY_NOTICES.md`.
