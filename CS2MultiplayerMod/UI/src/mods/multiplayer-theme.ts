// The game scales its UI through the root font size, so every rem in these components is a
// resolution-independent pixel.

// Shared opaque backdrop for the multiplayer form and all blocking connection
// screens. This matches the native flat-button fill used by the Join Game and
// Host Game tiles so the multiplayer surfaces use one consistent blue.
export const MULTIPLAYER_BLUE = "rgb(0, 25, 62)";

// Cohtml rejects var() fallbacks and expressions in shorthand properties. Use the
// same native radius as Chirper through its four supported longhand properties.
export const MULTIPLAYER_PANEL_CORNERS = {
    borderTopLeftRadius: "var(--panelRadius)",
    borderTopRightRadius: "var(--panelRadius)",
    borderBottomLeftRadius: "var(--panelRadius)",
    borderBottomRightRadius: "var(--panelRadius)",
};
export const MULTIPLAYER_HEADER_HEIGHT = "42rem";
