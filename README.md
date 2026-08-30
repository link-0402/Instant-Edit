# XIV Instant Edit

Instant Edit is a combination of in-game Dalamud plugin and Blender addon that allows for an easy and instant exchange of game models between the two (.mdl files through Penumbra as well as vanilla assets).

## Requirements

- [XIVLauncher](https://goatcorp.github.io/) with Dalamud enabled
- [Penumbra](https://github.com/xivdev/Penumbra)
- [Blender](https://www.blender.org/) 4.5.3+

## Installation

### Install the Blender add-on

1. Open **Edit > Preferences > Get Extensions** in Blender.
2. Click **Repositories**, click **+**, and choose **Add Remote Repository**.
3. Add this repository URL:

   `https://raw.githubusercontent.com/link-0402/XIV-Instant-Edit/main/Blender-Addon/blender_repo/index.json`
4. *Recommended* to tick "Check for Updates on Startup" to receive automatic updates.
5. Find **XIV Instant Edit** and install it if it hasn't already.

You can also download [XIV-Instant-Edit.zip](https://raw.githubusercontent.com/link-0402/XIV-Instant-Edit/main/Blender-Addon/blender_repo/XIV-Instant-Edit.zip)
and use **Install from Disk** in the same Blender preferences window.
Note that you will not receive automatic feature and compatibility updates this way and will have to update the add-on manually.

### Install the Dalamud plugin

1. In FFXIV, run `/xlsettings` and open **Experimental**.
2. Add this URL under **Custom Plugin Repositories**:

   `https://raw.githubusercontent.com/link-0402/XIV-Instant-Edit/main/Dalamud-Plugin/repo.json`

3. Enable the repository, save your settings, and open `/xlplugins`.
4. Find and install **XIV Instant Edit** under **All Plugins**.

## How to use it

1. Type /ie to open the plugin interface ingame. Click Refresh character list.
2. Start Blender. Verify The plugin shows Blender as "Online".
2. Verify import options, then click "Edit" on the model you want to import to Blender.
3. Do whatever you wanna do with the model in Blender.
4. Pick an export context from the list
- In-place overwrites the exact model that you imported.
- New Group sets up a new option group in the mod you imported from and automatically configures the paths for you.
- (Existing group) creates a new option in an existing option group.
- (Option in existing group overwrites the model file that is mapped to this option.
- Create Mashup creates a new mod or group in an existing mod containing the combined model + all required textures and materials. Only visible with 2+ mods imported into the scene, otherwise switches to "Create as new mod".
The context dropdown controls which mod structure is being shown.
5. Hit export. Immediately see the result ingame.
Creating new options on existing mods requires you to refresh the view in Penumbra by navigating to a different mod and back.

## Additional notes

### Main Features
Main Features
- One-click import of models through an on-screen browser or simplified mod file browser.
- Easy export context selection. Export in-place, pick any existing mod option or easily create a new one. The plugin sets up everything for you automatically.
- Instant creation of mashups. The plugin automatically sets up all required textures, materials and paths for you.
- Seamlessly integrates into any existing Blender scene, independent of body, devkit, etc.
- Simple Importer / Exporter for general FBX and MDL files with various QoL functions and automations optimized for FFXIV workflows 

### Material preview

The optional texture import uses the effective MTRL and TEX files resolved for the
selected model and packs the generated images into the Blender file. It is a
practical Principled BSDF approximation, not an exact reproduction of FFXIV's
shader pipeline. Character gear without a diffuse map is composed from its
colorset, index, normal, and mask textures. Missing or unsupported
resources keep the existing colored placeholder and produce a warning without
blocking model import. Note that the texture import is for preview purposes only.
Making changes to them in Blender will not affect the exported model.

## Contributing

Bug reports and feature idea submissions are welcome.
If you'd like to contribute anything, see [CONTRIBUTING.md](CONTRIBUTING.md) for setup, testing, and submission guidance.

## License

This is an unofficial community tool and is not affiliated with or endorsed by
Square Enix, Dalamud, Penumbra, or Blender. The project is licensed under the
[GNU GPL-3.0-or-later](LICENSE).
