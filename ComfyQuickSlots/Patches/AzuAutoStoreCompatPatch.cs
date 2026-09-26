namespace ComfyQuickSlots;

using System.Collections.Generic;
using System.Reflection;

using BepInEx.Bootstrap;
using BepInEx.Configuration;

using HarmonyLib;

using UnityEngine;
using UnityEngine.UI;

// AzuAutoStore's favoriting BorderRenderer (a postfix on InventoryGrid.UpdateGui) indexes
// m_elements[y * width + x] without bounds checks. Since Valheim 1.0 (Deep North) vanilla
// UpdateGui skips items whose grid position has no element, but AzuAutoStore throws
// ArgumentOutOfRangeException every frame. That aborts InventoryGui.Update, which leaves the
// crafting panel showing Iron Gate's placeholder description text. This replaces the routine
// with a bounds-checked copy. Only active when AzuAutoStore is loaded.
[HarmonyPatch]
static class AzuAutoStoreCompatPatch {
  const string AzuAutoStoreGuid = "Azumatt.AzuAutoStore";
  const string BorderName = "AzuAutoStoreFavoritingBorder";

  static MethodInfo _getPlayerConfig;
  static MethodInfo _isSlotFavorited;
  static MethodInfo _isItemNameFavorited;
  static MethodInfo _createBorderImage;
  static ConfigEntry<Color> _colorFavoritedItem;
  static ConfigEntry<Color> _colorFavoritedItemOnFavoritedSlot;
  static ConfigEntry<Color> _colorFavoritedSlot;

  static readonly HashSet<string> _loggedOffGridItems = new();

  static bool Prepare() {
    if (!Chainloader.PluginInfos.ContainsKey(AzuAutoStoreGuid)) {
      return false;
    }

    System.Type userConfig = AccessTools.TypeByName("AzuAutoStore.Patches.Favoriting.UserConfig");
    System.Type borderRenderer = AccessTools.TypeByName("AzuAutoStore.Patches.Favoriting.BorderRenderer");
    System.Type plugin = AccessTools.TypeByName("AzuAutoStore.AzuAutoStorePlugin");

    if (userConfig == null || borderRenderer == null || plugin == null) {
      ComfyQuickSlots.LogInfo("AzuAutoStore compat: expected types not found, skipping patch.");
      return false;
    }

    _getPlayerConfig = AccessTools.Method(userConfig, "GetPlayerConfig", new[] { typeof(long) });
    _isSlotFavorited = AccessTools.Method(userConfig, "IsSlotFavorited", new[] { typeof(Vector2i) });
    _isItemNameFavorited =
        AccessTools.Method(userConfig, "IsItemNameFavorited", new[] { typeof(ItemDrop.ItemData.SharedData) });
    _createBorderImage = AccessTools.Method(borderRenderer, "CreateBorderImage", new[] { typeof(Image) });
    _colorFavoritedItem = GetColorEntry(plugin, "BorderColorFavoritedItem");
    _colorFavoritedItemOnFavoritedSlot = GetColorEntry(plugin, "BorderColorFavoritedItemOnFavoritedSlot");
    _colorFavoritedSlot = GetColorEntry(plugin, "BorderColorFavoritedSlot");

    if (_getPlayerConfig == null
        || _isSlotFavorited == null
        || _isItemNameFavorited == null
        || _createBorderImage == null
        || TargetMethod() == null) {
      ComfyQuickSlots.LogInfo("AzuAutoStore compat: expected members not found, skipping patch.");
      return false;
    }

    return true;
  }

  static ConfigEntry<Color> GetColorEntry(System.Type plugin, string name) {
    return AccessTools.Field(plugin, name)?.GetValue(null) as ConfigEntry<Color>;
  }

  static MethodBase TargetMethod() {
    return AccessTools.Method("AzuAutoStore.Patches.Favoriting.BorderRenderer:UpdateGui");
  }

  // Original signature: (InventoryGrid __instance, Player player, Inventory ___m_inventory,
  // List<InventoryElement> ___m_elements). Arguments are taken by index because Harmony would
  // treat "___" names as instance field injections.
  [HarmonyPrefix]
  static bool UpdateGuiPrefix(Player __1, Inventory __2, List<InventoryElement> __3) {
    Player player = __1;
    Inventory inventory = __2;
    List<InventoryElement> elements = __3;

    if (!player || player.m_inventory != inventory) {
      return false;
    }

    int width = inventory.GetWidth();
    object playerConfig = _getPlayerConfig.Invoke(null, new object[] { player.GetPlayerID() });

    for (int y = 0; y < inventory.GetHeight(); y++) {
      for (int x = 0; x < width; x++) {
        Image border = GetBorder(elements, y * width + x);

        if (border) {
          border.color = _colorFavoritedSlot?.Value ?? Color.blue;
          border.enabled = (bool) _isSlotFavorited.Invoke(playerConfig, new object[] { new Vector2i(x, y) });
        }
      }
    }

    foreach (ItemDrop.ItemData item in inventory.m_inventory) {
      Image border = GetBorder(elements, item.m_gridPos.y * width + item.m_gridPos.x);

      if (!border) {
        if (_loggedOffGridItems.Add($"{item.m_shared.m_name}@{item.m_gridPos}")) {
          ComfyQuickSlots.LogInfo(
              $"AzuAutoStore compat: item {item.m_shared.m_name} x{item.m_stack} is at off-grid position "
                  + $"{item.m_gridPos} (inventory {width}x{inventory.GetHeight()}, {elements.Count} slots).");
        }

        continue;
      }

      if ((bool) _isItemNameFavorited.Invoke(playerConfig, new object[] { item.m_shared })) {
        border.color =
            border.enabled
                ? _colorFavoritedItemOnFavoritedSlot?.Value ?? Color.green
                : _colorFavoritedItem?.Value ?? Color.yellow;
        border.enabled = true;
      }
    }

    return false;
  }

  static Image GetBorder(List<InventoryElement> elements, int index) {
    if (index < 0 || index >= elements.Count || !elements[index] || !elements[index].m_queued) {
      return null;
    }

    Transform existing = Utils.FindChild(elements[index].m_queued.transform, BorderName);

    return existing
        ? existing.GetComponent<Image>()
        : (Image) _createBorderImage.Invoke(null, new object[] { elements[index].m_queued });
  }
}
