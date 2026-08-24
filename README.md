# Instant Edit

Instant Edit lets you edit visible FINAL FANTASY XIV models in Blender and
send the finished model back to its original Penumbra mod.

It currently supports model editing and export. Texture, material, and
animation editing are not included.

## Requirements

- [XIVLauncher](https://goatcorp.github.io/) with Dalamud enabled
- [Penumbra](https://github.com/xivdev/Penumbra)
- [Blender](https://www.blender.org/) 4.2+

## Installation

### Install the Blender add-on

1. Open **Edit > Preferences > Get Extensions** in Blender.
2. Click **Repositories**, click **+**, and choose **Add Remote Repository**.
3. Add this repository URL:

   `https://raw.githubusercontent.com/link-0402/Instant-Edit/main/Blender-Addon/blender_repo/index.json`

4. Find **XIV Instant Edit** and install it if it hasn't already.

You can also download [XIV-Instant-Edit.zip](https://github.com/link-0402/Instant-Edit/releases/download/1.0/XIV-Instant-Edit.zip)
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
3. Select a model from the list. Use **Refresh** if the model is not shown.
4. Edit the model in Blender. Instant Edit tools are available in the
   **XIV Instant Edit** tab of the 3D Viewport sidebar.
5. Click **Quick Export** to send the edited model back to its Penumbra mod.

Blender and FFXIV must be running at the same time. If the plugin reports that
Blender is offline, restart Blender with the add-on enabled.

Saved Instant Edit scenes reconnect their export context automatically when the
updated add-on and Dalamud plugin are running.

The Blender add-on also includes **Simple Export** for exporting visible mesh
objects as MDL, FBX, or glTF files, even if they haven't been imported through the IE plugin before.
In this case you may be required to configure the material names for mesh groups manually via the addon UI.

You may also save a model imported through IE as a variant, giving it a new file name. The addon will then
automatically setup the required Penumbra paths for this new model if the option is enabled.

## Notes

This is an unofficial community tool and is not affiliated with or endorsed by
Square Enix, Dalamud, Penumbra, or Blender. The project is licensed under the
[GNU GPL-3.0-or-later](LICENSE).
