# Instant Edit

Instant Edit lets you instantly send Final Fantasy XIV Penumbra models back and forth between
the game and Blender with just a single click. Models can be selected either from on-screen
resources belonging to your character or through a a simplified resource browser for your Penumbra mods.

## Requirements

- [XIVLauncher](https://goatcorp.github.io/) with Dalamud enabled
- [Penumbra](https://github.com/xivdev/Penumbra)
- [Blender](https://www.blender.org/) 4.5+

## Installation

### Install the Blender add-on

1. Open **Edit > Preferences > Get Extensions** in Blender.
2. Click **Repositories**, click **+**, and choose **Add Remote Repository**.
3. Add this repository URL:

   `https://raw.githubusercontent.com/link-0402/Instant-Edit/main/Blender-Addon/blender_repo/index.json`

4. Find **XIV Instant Edit** and install it if it hasn't already.

You can also download [XIV-Instant-Edit.zip](https://github.com/link-0402/Instant-Edit/tree/main/Blender-Addon/blender_repo/XIV-Instant-Edit.zip)
and use **Install from Disk** in the same Blender preferences window.
Note that you will not be getting automatic feature and compatability updates this way and have to update the plugin manually.

### Install the Dalamud plugin

1. In FFXIV, run `/xlsettings` and open **Experimental**.
2. Add this URL under **Custom Plugin Repositories**:

   `https://raw.githubusercontent.com/link-0402/Instant-Edit/main/Dalamud-Plugin/repo.json`

3. Enable the repository, save your settings, and open `/xlplugins`.
4. Find and install **Instant Edit** under **All Plugins**.

## How to use it

1. Start Blender and make sure the XIV Instant Edit add-on is enabled.
2. Start FFXIV and run `/ie` to open the plugin window.
3. Click **Refresh character list** at the top and browse to the model you are looking for, then click Edit.
   Enable **Apply textures and materials** first if you want Instant Edit to
   resolve the model's current game/Penumbra textures into a practical Blender
   viewport preview. The option is off by default. Enable **Exclude body and
   general materials** underneath it to leave body skin, body-piercing, and
   pube materials as placeholders while still previewing equipment materials.
4. Edit the model in Blender. Instant Edit tools are available in the
   **XIV Instant Edit** tab of the 3D Viewport sidebar.
5. Click **Quick Export** to send the edited model back to its Penumbra mod.
   If the scene contains multiple Instant Edit contexts and the destination is
   otherwise ambiguous, Quick Export asks you to choose the target first.

Blender and FFXIV must be running at the same time. If the plugin reports that
Blender is offline, restart Blender with the add-on enabled. In order for the communication
between game plugin and blender addon to work, the port configurations in the settings need to match.
I do not recommend changing them unless necessary and you know what you are doing.

The same Blender add-on preferences contain the Instant Edit cache settings.
Imports and Quick Exports are staged inside an add-on-owned `XIV-Instant-Edit`
subfolder of the selected cache directory. Automatic cleanup removes completed
jobs immediately and crash leftovers after 24 hours; **Clean Cache Now** removes
inactive owned jobs on demand.

Saved Instant Edit scenes reconnect their export context automatically when the
add-on and Dalamud plugin are running.

The Blender add-on also includes **Simple Import/Export** tabs for importing and exporting
MDL and FBX files through a traditional file selection without the game plugin.
In this case you may be required to configure the material names for mesh groups manually via the addon UI.

You may also save a model imported through IE as a variant by giving it a new file name. The addon will then
automatically setup the required Penumbra paths for this new model if the option is enabled, letting you switch back and forth
between the original and this new version right away.

### Material preview

The optional texture import uses the effective MTRL and TEX files resolved for the
selected model and packs the generated images into the Blender file. It is a
practical Principled BSDF approximation, not an exact reproduction of FFXIV's
shader pipeline. Character gear without a diffuse map is composed from its
colorset, index, normal, and mask textures. Missing or unsupported
resources keep the existing colored placeholder and produce a warning without
blocking model import. Note that the texture import is for preview purposes only.
Making changes to them in Blender will not affect the exported model.

## Notes

This is an unofficial community tool and is not affiliated with or endorsed by
Square Enix, Dalamud, Penumbra, or Blender. The project is licensed under the
[GNU GPL-3.0-or-later](LICENSE).
