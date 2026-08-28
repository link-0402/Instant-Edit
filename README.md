# XIV Instant Edit

XIV Instant Edit lets you instantly send Final Fantasy XIV Penumbra models back and forth between
the game and Blender with just a single click. Models can be selected either from on-screen
resources belonging to your character or through a simplified resource browser for your Penumbra mods.

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

1. Start Blender and make sure the XIV Instant Edit add-on is enabled.
2. Start FFXIV and run `/ie` to open the plugin window.
3. Click **Refresh character list** at the top and browse to the model you are looking for, then click Edit.
4. Edit the model in Blender. XIV Instant Edit tools are available in the
   **XIV Instant Edit** tab of the 3D Viewport sidebar.
5. (Optional) If you want to redirect the model export to another file/option group, pick a new target from the list.
   Default behavior is to export in-place, so replacing the exact model that was originally imported.
6. Click **Quick Export** to send the edited model back to its Penumbra mod.

The addon supports various other features, such as creating mashups on export, although
the workflow required for this should hopefully be fairly self-explanatory from the UI.


## Additional notes

### Settings

Blender and FFXIV must be running at the same time. If the plugin reports that
Blender is offline, restart Blender with the add-on enabled. In order for the communication
between the game plugin and Blender add-on to work, the port configurations in the settings need to match.
I do not recommend changing them unless necessary and you know what you are doing.

The same Blender add-on preferences contain the XIV Instant Edit cache settings.
Imports and Quick Exports are staged inside an add-on-owned `XIV-Instant-Edit`
subfolder of the selected cache directory. Automatic cleanup removes completed
jobs immediately and crash leftovers after 24 hours; **Clean Cache Now** removes
inactive owned jobs on demand.

You may also save a model imported through XIV Instant Edit as a variant by giving it a new file name. The add-on will then
automatically set up the required Penumbra paths for this new model if the option is enabled, letting you switch back and forth
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

## Contributing

Bug reports, feature requests, and pull requests are welcome. See
[CONTRIBUTING.md](CONTRIBUTING.md) for setup, testing, and submission guidance.

## License

This is an unofficial community tool and is not affiliated with or endorsed by
Square Enix, Dalamud, Penumbra, or Blender. The project is licensed under the
[GNU GPL-3.0-or-later](LICENSE).
