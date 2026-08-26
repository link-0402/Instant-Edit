# Modified for XIV Instant Edit, 2026. See MODIFICATIONS.md.
import bpy

from bpy.types import PropertyGroup
from bpy.props import StringProperty, IntProperty, BoolProperty, EnumProperty, CollectionProperty


_EXPORT_DESTINATION_ITEMS = []


def _export_destination_items(_self, context):
    global _EXPORT_DESTINATION_ITEMS
    from .context import context_collections, _value

    scene = getattr(context, "scene", None) or getattr(bpy.context, "scene", None)
    if scene is None:
        _EXPORT_DESTINATION_ITEMS = [
            (
                "ACTIVE",
                "Active Context",
                "Use the context associated with the active or selected model",
            )
        ]
        return _EXPORT_DESTINATION_ITEMS

    items = [
        (
            "ACTIVE",
            "Active Context",
            "Use the context associated with the active or selected model",
        )
    ]
    for collection in sorted(
        context_collections(scene),
        key=lambda value: str(_value(value, "source_game_path", "")).casefold(),
    ):
        context_id = str(_value(collection, "context_id", ""))
        game_path = str(_value(collection, "source_game_path", ""))
        model_name = game_path.replace("\\", "/").rsplit("/", 1)[-1] or context_id
        mod_name = str(_value(collection, "source_mod_name", ""))
        label = f"{model_name} ({mod_name})" if mod_name else model_name
        items.append((context_id, label, f"Overwrite the imported model at {game_path}"))
    # Blender requires dynamically generated enum strings to remain alive for
    # as long as the enum is in use.
    _EXPORT_DESTINATION_ITEMS = items
    return _EXPORT_DESTINATION_ITEMS


def _export_destination_changed(self, _context) -> None:
    """Never show targets cached for a different Quick Export context."""
    self.variant_target = "NEW_GROUP"
    self.variant_targets.clear()
    self.variant_targets_context_id = ""


def _save_as_variant_changed(self, _context) -> None:
    """A Penumbra option can only be generated for an actual variant."""
    if not self.save_as_variant:
        self.auto_setup_penumbra = False


class XIVIEVariantTarget(PropertyGroup):
    """One selectable Penumbra group or option returned by the plugin."""

    selection_id: StringProperty(default="", maxlen=256)  # type: ignore
    kind: StringProperty(default="", maxlen=16)  # type: ignore
    group_name: StringProperty(default="", maxlen=120)  # type: ignore
    option_name: StringProperty(default="", maxlen=120)  # type: ignore
    model_path: StringProperty(default="", maxlen=4096)  # type: ignore
    expanded: BoolProperty(default=True)  # type: ignore


class XIVIEInstantEditProps(PropertyGroup):

    show_utilities: BoolProperty(
        name="Toolbox",
        description="Show maintenance actions for Instant Edit context data",
        default=False,
    )  # type: ignore

    export_destination: EnumProperty(
        name="Export Destination",
        description="Choose which imported model destination Quick Export uses",
        items=_export_destination_items,
        update=_export_destination_changed,
    )  # type: ignore

    export_scope: EnumProperty(
        name="Export Parts",
        description="Choose which visible mesh objects Quick Export includes",
        default="VISIBLE",
        items=[
            ("VISIBLE", "All Visible", "Export every visible mesh object"),
            (
                "VISIBLE_NO_MANNEQUIN",
                "Visible Except Mannequin",
                "Export every visible mesh object except the object named Mannequin",
            ),
            (
                "CURRENT_COLLECTION",
                "Instant Edit Collection",
                "Export only visible mesh objects in the current Instant Edit collection",
            ),
        ],
    )  # type: ignore

    game_path: StringProperty(
        name="",
        description="Game path of the model loaded via Instant Edit",
        default="",
        maxlen=255,
    )  # type: ignore

    display_name: StringProperty(
        name="",
        description="Display name of the loaded model",
        default="",
        maxlen=255,
    )  # type: ignore

    object_index: IntProperty(
        name="",
        description="Index of the game object this model was picked from",
        default=-1,
    )  # type: ignore

    # These are a display/cache of the immutable collection reference. Safe
    # export reads the collection and tagged objects, not these scene values.
    context_id: StringProperty(
        name="",
        description="Active Instant Edit context identifier",
        default="",
        maxlen=256,
    )  # type: ignore

    context_schema: StringProperty(
        name="",
        description="Instant Edit context schema",
        default="",
        maxlen=128,
    )  # type: ignore

    context_version: IntProperty(
        name="",
        description="Instant Edit context schema version",
        default=0,
    )  # type: ignore

    plugin_instance_id: StringProperty(name="", default="", maxlen=256)  # type: ignore
    capability: StringProperty(name="", default="", maxlen=1024)  # type: ignore
    managed_destination: StringProperty(name="", default="", maxlen=4096)  # type: ignore
    last_export_id: StringProperty(name="", default="", maxlen=128)  # type: ignore

    last_status: StringProperty(
        name="",
        description="Status of the last Instant Edit action",
        default="Pick a model in-game via the Instant Edit plugin to get started.",
        maxlen=4096,
    )  # type: ignore

    save_as_variant: BoolProperty(
        name="Save as Variant",
        description="Save beside the original model under a different file name instead of replacing it",
        default=False,
        update=_save_as_variant_changed,
    )  # type: ignore

    variant_name: StringProperty(
        name="Variant Name",
        description="File name for the variant; .mdl is added automatically",
        default="",
        maxlen=128,
    )  # type: ignore

    auto_setup_penumbra: BoolProperty(
        name="Automatically setup in Penumbra",
        description="Automatically set up this new model in Penumbra on a new or existing option group",
        default=False,
    )  # type: ignore

    variant_group_name: StringProperty(
        name="Penumbra Option Group",
        description="Existing Penumbra option group to reuse, or name for a new group",
        default="New Group",
        maxlen=120,
    )  # type: ignore

    variant_target: StringProperty(
        name="Penumbra Target",
        description="The Penumbra group or option to receive this variant",
        default="NEW_GROUP",
        maxlen=256,
    )  # type: ignore

    variant_targets_context_id: StringProperty(default="", maxlen=256)  # type: ignore
    variant_targets: CollectionProperty(type=XIVIEVariantTarget)  # type: ignore

def get_instant_edit_props() -> XIVIEInstantEditProps:
    return bpy.context.scene.xiv_ie_instant_edit_props


def set_addon_properties() -> None:
    bpy.types.Scene.xiv_ie_instant_edit_props = bpy.props.PointerProperty(
        type=XIVIEInstantEditProps)


def remove_addon_properties() -> None:
    del bpy.types.Scene.xiv_ie_instant_edit_props


CLASSES = [
    XIVIEVariantTarget,
    XIVIEInstantEditProps,
]
