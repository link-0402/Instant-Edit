# Modification notice

This distribution is a modified and reduced derivative of Yet Another Addon.

Modifications made in 2026 include:

- extracting the FFXIV MDL import/export implementation into a standalone
  Blender extension;
- adding the Instant Edit local HTTP import and export protocol;
- adding capability negotiation and secure, isolated export contexts;
- adding in-place source-mod and variant exports through the Dalamud plugin;
- adding generated-armature and existing-scene-armature import modes;
- adapting Yet Another Addon's Mesh Studio overview for compact per-mesh
  object, part, attribute, material, and flow-data controls;
- allowing Quick Export to include all visible, name-defined mesh parts and groups;
- allowing duplicated Instant Edit meshes to be moved outside their source collection;
- adding SELF, ALL, and Glamourer-assisted redraw modes for Quick Export;
- allowing export through an Armature modifier without requiring object
  parenting;
- adding UV2 copy/clear, vertex color/alpha cleanup, and flow-data cleanup;
- adding a focused Simple Export operator and UI; and
- removing registration of unrelated outfit, pose, animation, modpack, and
  general utility tools.

The retained upstream implementation and these modifications are licensed
under GPL-3.0-or-later.
