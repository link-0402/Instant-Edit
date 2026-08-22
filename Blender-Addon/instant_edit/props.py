# Modified for XIV Instant Edit, 2026. See MODIFICATIONS.md.
import bpy

from bpy.types import PropertyGroup
from bpy.props import StringProperty, IntProperty, BoolProperty, EnumProperty


def _save_as_variant_changed(self, _context) -> None:
    """A Penumbra option can only be generated for an actual variant."""
    if not self.save_as_variant:
        self.auto_setup_penumbra = False


class XIVIEInstantEditProps(PropertyGroup):

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
        maxlen=512,
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
        description="Create and select a high-priority Penumbra option for this variant after exporting",
        default=False,
    )  # type: ignore

    redraw_mode: EnumProperty(
        name="Redraw",
        description="How characters should update after Quick Export",
        default="GLAM",
        items=[
            ("SELF", "SELF", "Redraw the character this model came from"),
            ("ALL", "ALL", "Redraw all currently available characters"),
            (
                "GLAM",
                "GLAM",
                "Let Glamourer update the character without a full redraw; not suitable for face models",
            ),
        ],
    )  # type: ignore


def get_instant_edit_props() -> XIVIEInstantEditProps:
    return bpy.context.scene.xiv_ie_instant_edit_props


def set_addon_properties() -> None:
    bpy.types.Scene.xiv_ie_instant_edit_props = bpy.props.PointerProperty(
        type=XIVIEInstantEditProps)


def remove_addon_properties() -> None:
    del bpy.types.Scene.xiv_ie_instant_edit_props


CLASSES = [
    XIVIEInstantEditProps,
]
