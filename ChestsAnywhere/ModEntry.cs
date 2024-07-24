using System;
using System.Collections.Generic;
using System.Linq;
using Force.DeepCloner;
using Microsoft.Xna.Framework;
using Pathoschild.Stardew.ChestsAnywhere.Framework;
using Pathoschild.Stardew.ChestsAnywhere.Framework.Containers;
using Pathoschild.Stardew.ChestsAnywhere.Menus.Overlays;
using Pathoschild.Stardew.Common;
using Pathoschild.Stardew.Common.Messages;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Inventories;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.Objects;

namespace Pathoschild.Stardew.ChestsAnywhere
{
    /// <summary>The mod entry point.</summary>
    internal class ModEntry : Mod
    {
        /*********
        ** Fields
        *********/
        /// <summary>The mod configuration.</summary>
        private ModConfig Config = null!; // set in Entry

        /// <summary>The configured key bindings.</summary>
        private ModConfigKeys Keys => this.Config.Controls;

        /// <summary>The internal mod settings.</summary>
        private ModData Data = null!; // set in Entry

        /// <summary>Encapsulates logic for finding chests.</summary>
        private ChestFactory ChestFactory = null!; // set in Entry

        /// <summary>The last selected chest.</summary>
        private readonly PerScreen<ManagedChest> LastChest = new();

        /// <summary>The menu instance for which the <see cref="CurrentOverlay"/> was created, if any.</summary>
        private readonly PerScreen<IClickableMenu> ForMenuInstance = new();

        /// <summary>The overlay for the current menu, which lets the player navigate and edit chests (or <c>null</c> if not applicable).</summary>
        private readonly PerScreen<IStorageOverlay?> CurrentOverlay = new();


        /*********
        ** Public methods
        *********/
        /// <inheritdoc />
        public override void Entry(IModHelper helper)
        {
            CommonHelper.RemoveObsoleteFiles(this, "ChestsAnywhere.pdb"); // removed in 1.22.7

            // initialize
            I18n.Init(helper.Translation);
            this.Config = helper.ReadConfig<ModConfig>();
            this.Data = helper.Data.ReadJsonFile<ModData>("assets/data.json") ?? new ModData();
            this.ChestFactory = new ChestFactory(helper.Multiplayer, () => this.Config);

            // Android workaround: shipping bin feature isn't compatible and breaks the UI
            if (Constants.TargetPlatform == GamePlatform.Android && this.Config.EnableShippingBin)
                this.Config.EnableShippingBin = false;

            // hook events
            helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
            helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
            helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
            helper.Events.GameLoop.UpdateTicking += this.OnUpdateTicking;
            helper.Events.Display.RenderedHud += this.OnRenderedHud;
            helper.Events.Input.ButtonsChanged += this.OnButtonsChanged;

            // validate translations
            if (!helper.Translation.GetTranslations().Any())
                this.Monitor.Log("The translation files in this mod's i18n folder seem to be missing. The mod will still work, but you'll see 'missing translation' messages. Try reinstalling the mod to fix this.", LogLevel.Warn);
        }

        /// <inheritdoc />
        public override object GetApi()
        {
            return new ChestsAnywhereApi(() => this.CurrentOverlay.Value);
        }


        /*********
        ** Private methods
        *********/
        /// <inheritdoc cref="IGameLoopEvents.GameLaunched"/>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event data.</param>
        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            // add Generic Mod Config Menu integration
            new GenericModConfigMenuIntegrationForChestsAnywhere(
                getConfig: () => this.Config,
                reset: () => this.Config = new ModConfig(),
                saveAndApply: () => this.Helper.WriteConfig(this.Config),
                modRegistry: this.Helper.ModRegistry,
                monitor: this.Monitor,
                manifest: this.ModManifest
            ).Register();
        }

        /// <inheritdoc cref="IGameLoopEvents.SaveLoaded"/>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event data.</param>
        private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
        {
            // validate game version
            string? versionError = this.ValidateGameVersion();
            if (versionError != null)
            {
                this.Monitor.Log(versionError, LogLevel.Error);
                CommonHelper.ShowErrorMessage(versionError);
            }

            // show multiplayer limitations warning
            if (!Context.IsMainPlayer)
                this.Monitor.Log("Multiplayer limitations: you can only access chests in synced locations since you're not the main player. This is due to limitations in the game's sync logic.", LogLevel.Info);
        }

        /// <inheritdoc cref="IDisplayEvents.RenderedHud"/>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event data.</param>
        private void OnRenderedHud(object? sender, RenderedHudEventArgs e)
        {
            // show chest label
            if (this.Config.ShowHoverTooltips)
            {
                ManagedChest? cursorChest = this.ChestFactory.GetChestFromTile(Game1.currentCursorTile);
                if (cursorChest != null && !cursorChest.HasDefaultName())
                {
                    Vector2 tooltipPosition = new Vector2(Game1.getMouseX(), Game1.getMouseY()) + new Vector2(Game1.tileSize / 2f);
                    CommonHelper.DrawHoverBox(e.SpriteBatch, cursorChest.DisplayName, tooltipPosition, Game1.uiViewport.Width - tooltipPosition.X - Game1.tileSize / 2f);
                }
            }
        }

        /// <inheritdoc cref="IGameLoopEvents.UpdateTicking"/>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event data.</param>
        private void OnUpdateTicking(object? sender, UpdateTickingEventArgs e)
        {
            this.ChangeOverlayIfNeeded();
        }

        /// <inheritdoc cref="IGameLoopEvents.UpdateTicked"/>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event data.</param>
        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            this.ChangeOverlayIfNeeded();
        }

        /// <inheritdoc cref="IInputEvents.ButtonsChanged"/>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event data.</param>
        private void OnButtonsChanged(object? sender, ButtonsChangedEventArgs e)
        {
            if (!Context.IsWorldReady)
                return;

            try
            {
                ModConfigKeys keys = this.Keys;

                // open menu
                if (keys.Toggle.JustPressed())
                {
                    // open if no conflict
                    if (Game1.activeClickableMenu == null)
                    {
                        if (Context.IsPlayerFree && !Game1.player.UsingTool && !Game1.player.isEating)
                            this.OpenMenu();
                    }

                    // open from inventory if it's safe to close the inventory screen
                    else if (Game1.activeClickableMenu is GameMenu gameMenu && gameMenu.currentTab == GameMenu.inventoryTab)
                    {
                        IClickableMenu inventoryPage = gameMenu.pages[GameMenu.inventoryTab];
                        if (inventoryPage.readyToClose())
                            this.OpenMenu();
                    }
                }

                // Auto sorter
                if (keys.AutoSortItems.JustPressed())
                {   
                    // Only do it if the player doesnt have a open menu
                    if (Game1.activeClickableMenu == null)
                        this.AutoSorter();
                }
            }
            catch (Exception ex)
            {
                this.HandleError(ex, "handling key input");
            }
        }

        /// <summary>Change the chest UI overlay if needed to match the current menu.</summary>
        /// <remarks>Since the menu gets reopened whenever the chest inventory changes, this method needs to be called before/after tick to avoid a visible UI flicker.</remarks>
        private void ChangeOverlayIfNeeded()
        {
            IClickableMenu menu = Game1.activeClickableMenu;

            // already matches menu
            if (this.ForMenuInstance.Value == menu)
                return;
            this.ForMenuInstance.Value = menu;

            // remove old overlay
            if (this.CurrentOverlay.Value != null)
            {
                this.CurrentOverlay.Value?.Dispose();
                this.CurrentOverlay.Value = null;
            }

            // get open chest
            ManagedChest? chest = this.ChestFactory.GetChestFromMenu(menu);
            if (chest == null)
                return;

            // reopen shipping box in standard chest UI if needed
            // This is called in two cases:
            // - When the player opens the shipping bin directly, it opens the shipping bin view instead of the full chest view.
            // - When the player changes the items in the chest view, it reopens itself but loses the constructor args (e.g. highlight function).
            if (this.Config.EnableShippingBin && chest.Container is ShippingBinContainer)
            {
                if (menu is ItemGrabMenu chestMenu && (!chestMenu.showReceivingMenu || chestMenu.inventory.highlightMethod?.Target is not ShippingBinContainer))
                    this.ForMenuInstance.Value = menu = (ItemGrabMenu)chest.OpenMenu();
            }

            // add overlay
            RangeHandler range = this.GetCurrentRange();
            ManagedChest[] chests = this.ChestFactory.GetChests(range, excludeHidden: true, alwaysInclude: chest).ToArray();
            bool isAutomateInstalled = this.Helper.ModRegistry.IsLoaded("Pathoschild.Automate");
            switch (menu)
            {
                case ItemGrabMenu chestMenu:
                    this.CurrentOverlay.Value = new ChestOverlay(chestMenu, chest, chests, this.Config, this.Keys, this.Helper.Events, this.Helper.Input, this.Helper.Reflection, showAutomateOptions: isAutomateInstalled && chest.CanConfigureAutomate);
                    break;

                case ShopMenu shopMenu:
                    this.CurrentOverlay.Value = new ShopMenuOverlay(shopMenu, chest, chests, this.Config, this.Keys, this.Helper.Events, this.Helper.Input, this.Helper.Reflection, showAutomateOptions: isAutomateInstalled && chest.CanConfigureAutomate);
                    break;
            }

            // hook new overlay
            if (this.CurrentOverlay.Value is { } overlay)
            {
                overlay.OnChestSelected += selected =>
                {
                    this.LastChest.Value = selected;
                    selected.OpenMenu();
                };
                this.CurrentOverlay.Value.OnAutomateOptionsChanged += this.NotifyAutomateOfChestUpdate;
            }
        }

        /// <summary>Open the menu UI.</summary>
        private void OpenMenu()
        {
            if (this.Config.Range == ChestRange.None)
                return;

            // handle disabled location
            if (this.IsDisabledLocation(Game1.currentLocation))
            {
                CommonHelper.ShowInfoMessage(I18n.Errors_DisabledFromHere(), duration: 1000);
                return;
            }

            // get chests
            RangeHandler range = this.GetCurrentRange();
            ManagedChest[] chests = this.ChestFactory.GetChests(range, excludeHidden: true).ToArray();
            ManagedChest? selectedChest =
                ChestFactory.GetBestMatch(chests, this.LastChest.Value)
                ?? chests.FirstOrDefault(p => object.ReferenceEquals(p.Location, Game1.currentLocation))
                ?? chests.FirstOrDefault();

            // show error
            if (selectedChest == null)
            {
                CommonHelper.ShowInfoMessage(this.GetNoChestsFoundError(), duration: 1000);
                return;
            }

            // render menu
            selectedChest.OpenMenu();
        }

        /// <summary>Notify Automate that a chest's automation options updated.</summary>
        /// <param name="chest">The chest that was updated.</param>
        private void NotifyAutomateOfChestUpdate(ManagedChest chest)
        {
            long hostId = Game1.MasterPlayer.UniqueMultiplayerID;
            var message = new AutomateUpdateChestMessage { LocationName = chest.Location.NameOrUniqueName, Tile = chest.Tile };
            this.Helper.Multiplayer.SendMessage(message, nameof(AutomateUpdateChestMessage), modIDs: ["Pathoschild.Automate"], playerIDs: [hostId]);
        }

        /// <summary>Validate that the game versions match the minimum requirements, and return an appropriate error message if not.</summary>
        private string? ValidateGameVersion()
        {
            if (Constant.MinimumApiVersion.IsNewerThan(Constants.ApiVersion))
                return $"The Chests Anywhere mod requires a newer version of SMAPI. Please update SMAPI from {Constants.ApiVersion} to {Constant.MinimumApiVersion}.";

            return null;
        }

        /// <summary>Log an error and warn the user.</summary>
        /// <param name="ex">The exception to handle.</param>
        /// <param name="verb">The verb describing where the error occurred (e.g. "looking that up").</param>
        private void HandleError(Exception ex, string verb)
        {
            this.Monitor.Log($"Something went wrong {verb}:\n{ex}", LogLevel.Error);
            CommonHelper.ShowErrorMessage($"Huh. Something went wrong {verb}. The error log has the technical details.");
        }

        /// <summary>Get whether remote access is disabled from the given location.</summary>
        /// <param name="location">The game location.</param>
        private bool IsDisabledLocation(GameLocation location)
        {
            return
                this.Config.DisabledInLocations.Contains(location.Name)
                || (location is MineShaft && location.Name.StartsWith("UndergroundMine") && this.Config.DisabledInLocations.Contains("UndergroundMine"));
        }

        /// <summary>Get the range for the current context.</summary>
        private RangeHandler GetCurrentRange()
        {
            ChestRange range = this.IsDisabledLocation(Game1.currentLocation)
                ? ChestRange.None
                : this.Config.Range;
            return new RangeHandler(this.Data.WorldAreas, range, Game1.currentLocation);
        }

        /// <summary>Get the error translation to show if no chests were found.</summary>
        private string GetNoChestsFoundError()
        {
            if (this.Config.Range == ChestRange.CurrentLocation || !Context.IsMainPlayer)
                return I18n.Errors_NoChestsInLocation();

            if (this.Config.Range != ChestRange.Unlimited)
                return I18n.Errors_NoChestsInRange();

            return I18n.Errors_NoChests();
        }

        /// <summary>Get the auto sort range for the current context.</summary>
        private RangeHandler GetAutoSortCurrentRange()
        {
            ChestRange range = this.IsDisabledLocation(Game1.currentLocation)
                ? ChestRange.None
                : this.Config.AutoSortRange;
            return new RangeHandler(this.Data.WorldAreas, range, Game1.currentLocation);
        }

        /// <summary>Auto sort the player items into chests.</summary>
        private void AutoSorter()
        {
            if (this.Config.EnableAutoSort)
            {
                bool somethingWasSorted = false;
                bool stopLookingForThisItem;
                
                RangeHandler range = this.GetAutoSortCurrentRange();
                ManagedChest[] managedChests = this.ChestFactory.GetChests(range, true).ToArray();

                // For each item in the player inventory
                foreach(var item in Game1.player.Items)
                {
                    stopLookingForThisItem = false;

                    if (item != null)
                    {
                        // For each chest in the range
                        foreach(var managedChest in managedChests)
                        {
                            // TODO - Maybe let the player configure where to auto sort
                            if (managedChest.MapEntity is Chest)
                            {
                                Chest chest = (Chest)managedChest.MapEntity;
                                Item?[] chestItems = managedChest.Container.Inventory.ToArray();

                                // Check each chest inventory slot 
                                foreach(var chestItem in chestItems)
                                {
                                    // Skip this chest slot if it is empty
                                    if (chestItem == null)
                                        continue;

                                    // Check for the item match
                                    if (chestItem.Name == item.Name && chestItem.Quality == item.Quality)
                                    {
                                        // if stack is full, try to search for other stack inside the chest
                                        if (chestItem.Stack == chestItem.maximumStackSize())
                                            continue;

                                        // If stack is half full, fill it and search other stack
                                        else if ((chestItem.Stack + item.Stack) >= chestItem.maximumStackSize())
                                        {
                                            int toStore = chestItem.maximumStackSize() - chestItem.Stack;
                                            Item toAdd = item.DeepClone();
                                            toAdd.Stack = toStore;
                                            chest.addItem(toAdd);

                                            // Avoid a bug where we left a "ghost" stack to the player
                                            if ((item.Stack - toStore) == 0)
                                                Game1.player.removeItemFromInventory(item);
                                            else
                                                item.Stack -= toStore;

                                            somethingWasSorted = true;
                                            continue;
                                        }

                                        // But, if we can store the entire stack, just do it
                                        else
                                        {
                                            chest.addItem(item);
                                            Game1.player.removeItemFromInventory(item);
                                            somethingWasSorted = true;

                                            // If we sorted the entire stack, we can stop look into the chests
                                            stopLookingForThisItem = true;
                                            break;
                                        }
                                    }
                                }
                            }

                            // Stop the search into the chests
                            if (stopLookingForThisItem)
                                break;
                        }
                    }
                }

                // Showing a simple message saying if the item was sorted
                if (somethingWasSorted)
                    Game1.addHUDMessage(new HUDMessage(I18n.Alert_AutoSorter_Sorted(), HUDMessage.newQuest_type) { timeLeft = 1000 });
                else
                    Game1.addHUDMessage(new HUDMessage(I18n.Alert_AutoSorter_NotSorted(), HUDMessage.error_type) { timeLeft = 1000 });
            }
        }
    }
}
