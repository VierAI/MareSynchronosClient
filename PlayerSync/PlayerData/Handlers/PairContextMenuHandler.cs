using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using MareSynchronos.API.Data.Extensions;
using MareSynchronos.Interop.Ipc;
using MareSynchronos.MareConfiguration;
using MareSynchronos.PlayerData.Pairs;
using MareSynchronos.Services;
using MareSynchronos.Services.Mediator;
using MareSynchronos.Services.ServerConfiguration;
using MareSynchronos.MareConfiguration.Models;
using MareSynchronos.Utils;
using MareSynchronos.WebAPI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;


namespace MareSynchronos.PlayerData.Handlers
{
    public enum ContextMenuItemId
    {
        None = 0,
        OpenProfile,
        PausePair,
        PairData,
        InviteToSyncshell,
        AddToOverrides,
        ReapplyLastData,
        ChangePermissions,
        CyclePauseState,
    }

    public static class ContextMenuSettings
    {
        public static ContextMenuItemId[] Order { get; set; } = new ContextMenuItemId[6]
        {
        ContextMenuItemId.OpenProfile,
        ContextMenuItemId.PausePair,
        ContextMenuItemId.PairData,
        ContextMenuItemId.InviteToSyncshell,
        ContextMenuItemId.AddToOverrides,
        ContextMenuItemId.None
        };
        public static bool[] SPriority { get; set; } = new bool[6];
    }

    public class PairContextMenuHandler : DisposableMediatorSubscriberBase, IHostedService
    {
        private readonly IContextMenu _dalamudContextMenu;
        private readonly MareConfigService _configurationService;
        private readonly DalamudUtilService _dalamudUtilService;
        private readonly PairManager _pairManager;
        private readonly ApiController _apiController;
        private readonly ServerConfigurationManager _serverConfigurationManager;
        private readonly PairInviteManager _inviteManager;
        private readonly PlayerPerformanceConfigService _performanceConfigService;
        private readonly IpcManager _ipcManager;

        public PairContextMenuHandler(ILogger<PairContextMenuHandler> logger, MareMediator mediator, IContextMenu dalamudContextMenu,
            MareConfigService mareConfigService, DalamudUtilService dalamudUtilService, PairManager pairManager,
            ApiController apiController, ServerConfigurationManager serverConfigurationManager,
            PairInviteManager pairInviteManager, PlayerPerformanceConfigService playerPerformanceConfigService,
            IpcManager ipcManager) : base(logger, mediator)
        {
            _dalamudContextMenu = dalamudContextMenu;
            _dalamudUtilService = dalamudUtilService;
            _configurationService = mareConfigService;
            _pairManager = pairManager;
            _apiController = apiController;
            _serverConfigurationManager = serverConfigurationManager;
            _inviteManager = pairInviteManager;
            _performanceConfigService = playerPerformanceConfigService;
            _ipcManager = ipcManager;
        }

        private string SelfUID => _apiController.UID;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Logger.LogDebug("Context Menu Handler started.");
            _dalamudContextMenu.OnMenuOpened += DalamudContextMenuOnOnOpenGameObjectContextMenu;

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Logger.LogDebug("Context Menu Handler stopped.");
            _dalamudContextMenu.OnMenuOpened -= DalamudContextMenuOnOnOpenGameObjectContextMenu;
            UnsubscribeAll();

            return Task.CompletedTask;
        }

        private void DalamudContextMenuOnOnOpenGameObjectContextMenu(IMenuOpenedArgs args)
        {
            if (args.MenuType == ContextMenuType.Inventory) return;
            if (!_configurationService.Current.EnableRightClickMenus) return;

            Logger.LogTrace("Addon called: {addon}", args.AddonName);

            if (string.Equals(args.AddonName, "ChatLog", StringComparison.OrdinalIgnoreCase))
                AddUserPairChatContextMenu(args);

            var target = _dalamudUtilService.SoftTargetAddress != nint.Zero ? _dalamudUtilService.SoftTargetAddress : _dalamudUtilService.TargetAddress;
            // 1.15.5.1408: the auto-pause override is resolved from the right-clicked object itself (or its content id), not from
            // the soft/hard target, so it shows even when the clicked player is not the current target or has no cached player.
            if (args.Target is MenuTargetDefault autoTarget)
            {
                var autoPair = PairAutoPauseOverride.ResolveMenuPair(autoTarget, _pairManager, _dalamudUtilService);
                var hasReason = PairAutoPauseOverride.TryGetAutoPauseReason(autoPair, _serverConfigurationManager, out var autoReason);
                Logger.LogDebug("Auto-pause override: addon {addon}, menu {type}, pair {uid}, paused {paused}, reason {reason}, shown {shown}",
                    args.AddonName ?? "world", args.MenuType, autoPair?.UserData.UID ?? "none", autoPair?.IsPaused ?? false, autoReason, hasReason);
                if (hasReason && autoPair != null) PairAutoPauseOverride.AddMenuItem(args, autoPair, autoReason, _performanceConfigService, Mediator);
            }

            if (target == nint.Zero) return;
            var pairs = _pairManager.GetVisiblePairs();
            var currentTargetPair = pairs.SingleOrDefault(u => u.Address == target);
            if (currentTargetPair != null)
                AddContextMenu(currentTargetPair, args);
            else if (args.Target is MenuTargetDefault pausedTarget && pausedTarget.TargetObject?.Address == target)
            {
                // 1.15.5.1220: paused pairs (no cached player) get the regular menu too, found by the target's hashed content id.
                var pausedPair = PairAutoPauseOverride.FindPausedPair(target, _pairManager, _dalamudUtilService);
                if (pausedPair != null && pausedPair.IsPaused) AddContextMenu(pausedPair, args, paused: true);
            }
        }

        private void AddContextMenu(Pair pair, IMenuOpenedArgs args, bool paused = false)
        {
            if (args.Target is not MenuTargetDefault target) return;
            if (!paused && (!pair.HasCachedPlayer || target.TargetObjectId != pair.PlayerCharacterId || pair.IsPaused)) return;
            AddBudgetExemptItem(pair, args);
            // Fork (PairMenuSwap): same conditions as PairInviteManager's Send Pair Request (world menu, not a two-way direct pair)
            var worldMenu = args.AddonName == null;

            for (int ss = 0; ss < _configurationService.Current.ContextMenuOrder.Length; ss++)
            {
                var itemS = _configurationService.Current.ContextMenuOrder[ss];
                if (itemS == ContextMenuItemId.None) continue;

                int pri = _configurationService.Current.SPriority[ss] ? -1 : 0;
                switch (itemS)
                {
                    case ContextMenuItemId.OpenProfile:
                        args.AddMenuItem(new MenuItem()
                        {
                            Name = new SeStringBuilder().AddText("Open Profile").Build(),
                            OnClicked = (a) => Mediator.Publish(new ProfileOpenStandaloneMessage(pair)),
                            UseDefaultPrefix = false,
                            PrefixChar = 'P',
                            PrefixColor = 530,
                            Priority = pri,
                            //Priority = -1, // you can move this to the top with -1
                        });
                        break;

                    case ContextMenuItemId.PairData:
                        args.AddMenuItem(new MenuItem()
                        {
                            Name = new SeStringBuilder().AddText("Pair Data").Build(),
                            OnClicked = (a) => DrawPairDataContenxtSubmenu(pair, a, worldMenu && !paused),
                            IsSubmenu = true,
                            UseDefaultPrefix = false,
                            PrefixChar = 'P',
                            PrefixColor = 530,
                            Priority = pri,
                        });
                        break;

                    case ContextMenuItemId.InviteToSyncshell:
                        args.AddMenuItem(new MenuItem()
                        {
                            Name = new SeStringBuilder().AddText("Invite to Syncshell").Build(),
                            OnClicked = (args) => DrawSyncshellInviteContenxtSubmenu(pair, args),
                            IsSubmenu = true,
                            UseDefaultPrefix = false,
                            PrefixChar = 'P',
                            PrefixColor = 530,
                            Priority = pri,
                        });
                        break;

                    case ContextMenuItemId.AddToOverrides:
                        args.AddMenuItem(new MenuItem()
                        {
                            Name = new SeStringBuilder().AddText("Add to Overrides").Build(),
                            OnClicked = (args) => DrawAddToOverridesContenxtSubmenu(pair, args),
                            IsSubmenu = true,
                            UseDefaultPrefix = false,
                            PrefixChar = 'P',
                            PrefixColor = 530,
                            Priority = pri,
                        });
                        break;

                    case ContextMenuItemId.PausePair:
                        args.AddMenuItem(new MenuItem()
                        {
                            Name = new SeStringBuilder().AddText($"Pause {pair.UserData.AliasOrUID}").Build(),
                            OnClicked = (args) => DrawPairPauseSubmenu(pair, args),
                            IsSubmenu = true,
                            UseDefaultPrefix = false,
                            PrefixChar = 'P',
                            PrefixColor = 17,
                            Priority = pri,
                        });
                        break;

                    case ContextMenuItemId.ReapplyLastData:
                        if (paused) break; // nothing applied while paused
                        args.AddMenuItem(new MenuItem()
                        {
                            Name = new SeStringBuilder().AddText("Reapply Last Data").Build(),
                            OnClicked = (a) => pair.ReapplyCachedDataByUser(),
                            UseDefaultPrefix = false,
                            PrefixChar = 'P',
                            PrefixColor = 530,
                            Priority = pri,
                        });
                        break;

                    case ContextMenuItemId.ChangePermissions:
                        args.AddMenuItem(new MenuItem()
                        {
                            Name = new SeStringBuilder().AddText("Change Permissions").Build(),
                            OnClicked = (a) => Mediator.Publish(new OpenPermissionWindow(pair)),
                            UseDefaultPrefix = false,
                            PrefixChar = 'P',
                            PrefixColor = 530,
                            Priority = pri,
                        });
                        break;

                    case ContextMenuItemId.CyclePauseState:
                        args.AddMenuItem(new MenuItem()
                        {
                            Name = new SeStringBuilder().AddText("Cycle Pause State").Build(),
                            OnClicked = (a) => Mediator.Publish(new CyclePauseMessage(pair.UserData)),
                            UseDefaultPrefix = false,
                            PrefixChar = 'P',
                            PrefixColor = 530,
                            Priority = pri,
                        });
                        break;
                }
            }
        }

        private void AddBudgetExemptItem(Pair pair, IMenuOpenedArgs args)
        {
            var uid = pair.UserData.UID;
            if (!VierAIConfigStore.Current.MemoryBudgetEnabled && !PSyncCrowd.IsBudgetHeld(uid)) return;
            var exempt = PSyncBudgetExempt.IsExempt(uid);
            args.AddMenuItem(new MenuItem()
            {
                Name = new SeStringBuilder().AddText(exempt ? "Remove memory budget exemption" : "Exempt from memory budget").Build(),
                OnClicked = _ => PSyncBudgetExempt.Toggle(pair),
                UseDefaultPrefix = false,
                PrefixChar = 'P',
                PrefixColor = 530,
            });
        }

        private void DrawPairDataContenxtSubmenu(Pair pair, IMenuItemClickedArgs clickedArgs, bool worldMenu = false)
        {
            var menuItems = new List<MenuItem>();
            var paused = pair.IsPaused;

            // Fork (PairMenuSwap): Send Pair Request replaces Reapply Last Data here when Reapply is at the top level instead
            var swapIdent = worldMenu && _apiController.IsConnected && pair.UserPair?.IndividualPairStatus != API.Data.Enum.IndividualPairStatus.Bidirectional
                ? _dalamudUtilService.GetHashedCIDFromPlayerPointer(pair.Address) : null;
            if (swapIdent != null) menuItems.Add(PairMenuSwap.SendPairRequestItem(() => _inviteManager.SendPairRequest(swapIdent)));
            else if (!paused) menuItems.Add(new MenuItem()
            {
                Name = new SeStringBuilder().AddText("Reapply Last Data").Build(),
                OnClicked = (a) => pair.ReapplyCachedDataByUser()
            });

            menuItems.Add(new MenuItem()
            {
                Name = new SeStringBuilder().AddText("Change Permissions").Build(),
                OnClicked = (a) => Mediator.Publish(new OpenPermissionWindow(pair))
            });

            menuItems.Add(new MenuItem()
            {
                Name = new SeStringBuilder().AddText("Cycle Pause State").Build(),
                OnClicked = (a) => Mediator.Publish(new CyclePauseMessage(pair.UserData)),
            });

            PairMenuPrioritize.AddItem(menuItems, pair, _performanceConfigService, Mediator);

            // 1.15.5.1217: force re-download and the hard blocklist (inside this submenu, so the top-level layout is unchanged).
            if (!paused) menuItems.Add(new MenuItem()
            {
                Name = new SeStringBuilder().AddText("Force Re-download").Build(),
                OnClicked = (a) => _ = pair.ForceRedownloadAsync()
            });

            var isBlocked = MareSynchronos.Services.PSyncCrowd.IsBlocked(pair);
            menuItems.Add(new MenuItem()
            {
                Name = new SeStringBuilder().AddText(isBlocked ? "Unblock (allow downloads)" : "Block (no downloads or applies)").Build(),
                OnClicked = (a) => MareSynchronos.Services.PSyncBlocklist.Toggle(pair)
            });

            menuItems.Add(new MenuItem()
            {
                Name = new SeStringBuilder().AddText("Return").Build(),
                IsReturn = true,
                OnClicked = _ => _dalamudUtilService.OpenContextMenu(clickedArgs.AgentPtr)
            });

            clickedArgs.OpenSubmenu(new SeStringBuilder().AddText("PlayerSync Pair Data").Build(), menuItems);
        }

        private void DrawSyncshellInviteContenxtSubmenu(Pair pair, IMenuItemClickedArgs clickedArgs)
        {
            var menuItems = new List<MenuItem>();

            var maxPairs = _apiController.ServerInfo.MaxGroupUserCount - 1;

            var pairCountsByGroupId = _pairManager.PairsWithGroups
                .SelectMany(pairGroups => pairGroups.Value.Select(group => group.GID))
                .GroupBy(gid => gid, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(grouping => grouping.Key, grouping => grouping.Count(), StringComparer.OrdinalIgnoreCase);

            var syncshellsOwnerOrMod = _pairManager.Groups
                .Where(group => group.Value.GroupUserInfo.IsModerator() || group.Value.OwnerUID == SelfUID);

            var openSyncshells = _pairManager.Groups
                .Where(group => 
                (group.Value.PublicData.KnownPasswordless || group.Value.GroupPermissions.IsEnableGuestMode()) 
                && !group.Value.GroupPermissions.IsDisableInvites());

            var sharedSyncshellIds = _pairManager.PairsWithGroups
                .Where(pairGroup => pairGroup.Key.UserData.UID == pair.UserData.UID)
                .SelectMany(pairGroup => pairGroup.Value.Select(group => group.GID))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var finalSyncshells = syncshellsOwnerOrMod
                .Concat(openSyncshells)
                .DistinctBy(group => group.Key.GID, StringComparer.OrdinalIgnoreCase)
                .Where(group => !sharedSyncshellIds.Contains(group.Key.GID))
                .Where(group => !pairCountsByGroupId.TryGetValue(group.Key.GID, out var count) || count < maxPairs)
                .OrderBy(group => group.Key.GID)
                .ToList();

            if (finalSyncshells.Count == 0)
            {
                menuItems.Add(new MenuItem()
                {
                    Name = new SeStringBuilder().AddText("No Syncshells with invite access").Build(),
                    IsEnabled = false
                });
            }
            else
            {
                foreach (var syncshell in finalSyncshells)
                {
                    var shell = syncshell.Value.Group;
                    var alias = String.IsNullOrWhiteSpace(shell.Alias) ? "" : $" ({shell.Alias})";
                    menuItems.Add(new MenuItem()
                    {
                        Name = new SeStringBuilder().AddText($"{shell.GID}{alias}").Build(),
                        OnClicked = _ => _inviteManager.SendGroupInvite(new(syncshell.Key, pair.UserData))
                    });
                }
            }

            menuItems.Add(new MenuItem()
                {
                    Name = new SeStringBuilder().AddText("Return").Build(),
                    IsReturn = true,
                    OnClicked = _ => _dalamudUtilService.OpenContextMenu(clickedArgs.AgentPtr)
                });

            clickedArgs.OpenSubmenu(new SeStringBuilder().AddText("Invite to Syncshell").Build(), menuItems);
        }

        private void DrawAddToOverridesContenxtSubmenu(Pair pair, IMenuItemClickedArgs clickedArgs)
        {
            var pairUID = pair.UserData.UID;

            var menuItems = new List<MenuItem>();

            menuItems.Add(new MenuItem()
            {
                Name = new SeStringBuilder().AddText("Mod Filtering").Build(),
                OnClicked = _ =>
                {
                    if (!_configurationService.Current.UIDsToOverrideFilter.Contains(pairUID, StringComparer.Ordinal))
                    {
                        _configurationService.Current.UIDsToOverrideFilter.Add(pairUID);
                        _configurationService.Save();
                        Mediator.Publish(new CyclePauseMessage(pair.UserData));
                    }
                }
            });

            menuItems.Add(new MenuItem()
            {
                Name = new SeStringBuilder().AddText("Auto Compression").Build(),
                OnClicked = _ =>
                {
                    if (!_performanceConfigService.Current.UIDsToOverride.Contains(pairUID, StringComparer.Ordinal))
                    {
                        _performanceConfigService.Current.UIDsToOverride.Add(pairUID);
                        _performanceConfigService.Save();
                        Mediator.Publish(new CyclePauseMessage(pair.UserData));
                    }
                }
            });

            menuItems.Add(new MenuItem()
            {
                Name = new SeStringBuilder().AddText("Auto Threshold Pausing").Build(),
                OnClicked = _ =>
                {
                    if (!_performanceConfigService.Current.UIDsToIgnore.Contains(pairUID, StringComparer.Ordinal))
                    {
                        _performanceConfigService.Current.UIDsToIgnore.Add(pairUID);
                        _performanceConfigService.Save();
                        Mediator.Publish(new CyclePauseMessage(pair.UserData));
                    }
                }
            });

            menuItems.Add(new MenuItem()
            {
                Name = new SeStringBuilder().AddText("Auto Height Pausing").Build(),
                OnClicked = _ =>
                {
                    if (!_performanceConfigService.Current.UIDsToIgnoreForHeightPausing.Contains(pairUID, StringComparer.Ordinal))
                    {
                        _performanceConfigService.Current.UIDsToIgnoreForHeightPausing.Add(pairUID);
                        _performanceConfigService.Save();
                        Mediator.Publish(new CyclePauseMessage(pair.UserData));
                    }
                }
            });

            menuItems.Add(new MenuItem()
            {
                Name = new SeStringBuilder().AddText("Return").Build(),
                IsReturn = true,
                OnClicked = _ => _dalamudUtilService.OpenContextMenu(clickedArgs.AgentPtr)
            });

            clickedArgs.OpenSubmenu(new SeStringBuilder().AddText("Add to Overrides").Build(), menuItems);
        }

        private void DrawPairPauseSubmenu(Pair pair, IMenuItemClickedArgs clickedArgs)
        {
            var menuItems = new List<MenuItem>();

            menuItems.Add(new MenuItem()
            {
                Name = new SeStringBuilder().AddText("Pause").Build(),
                OnClicked = (a) => Mediator.Publish(new PauseMessage(pair.UserData, PauseReason.Manual, PauseDuration.Indefinitely)),
                UseDefaultPrefix = false,
                PrefixChar = 'P',
                PrefixColor = 17,
            });

            menuItems.Add(new MenuItem()
            {
                Name = new SeStringBuilder().AddText("Pause for 30 minutes").Build(),
                OnClicked = (a) => Mediator.Publish(new PauseMessage(pair.UserData, PauseReason.Manual, PauseDuration.ThirtyMinutes)),
                UseDefaultPrefix = false,
                PrefixChar = 'P',
                PrefixColor = 17,
            });

            menuItems.Add(new MenuItem()
            {
                Name = new SeStringBuilder().AddText("Pause for 4 hours").Build(),
                OnClicked = (a) => Mediator.Publish(new PauseMessage(pair.UserData, PauseReason.Manual, PauseDuration.FourHours)),
                UseDefaultPrefix = false,
                PrefixChar = 'P',
                PrefixColor = 17,
            });

            menuItems.Add(new MenuItem()
            {
                Name = new SeStringBuilder().AddText("Pause for 8 hours").Build(),
                OnClicked = (a) => Mediator.Publish(new PauseMessage(pair.UserData, PauseReason.Manual, PauseDuration.EightHours)),
                UseDefaultPrefix = false,
                PrefixChar = 'P',
                PrefixColor = 17,
            });

            menuItems.Add(new MenuItem()
            {
                Name = new SeStringBuilder().AddText("Block Pairing").Build(),
                OnClicked = (a) => Mediator.Publish(new UserPairStickyPauseAndRemoveMessage(pair.UserData)),
                UseDefaultPrefix = false,
                PrefixChar = 'P',
                PrefixColor = 17,
            });

            menuItems.Add(new MenuItem()
            {
                Name = new SeStringBuilder().AddText("Return").Build(),
                IsReturn = true,
                OnClicked = _ => _dalamudUtilService.OpenContextMenu(clickedArgs.AgentPtr)
            });

            clickedArgs.OpenSubmenu(new SeStringBuilder().AddText($"Pair {pair.UserData.AliasOrUID}").Build(), menuItems);
        }

        private void AddUserPairChatContextMenu(IMenuOpenedArgs args)
        {
            if (args.Target is not MenuTargetDefault target)
                return;

            var cid = target.TargetContentId.ToString().GetHash256();

            var pair = _pairManager.GetPairByCID(cid);
            if (pair == null || pair.IsPaused) return;

            args.AddMenuItem(new MenuItem()
            {
                Name = new SeStringBuilder().AddText("Send Lifestream Location Invite").Build(),
                OnClicked = (args) => DrawLifestreamInviteSubmenu(pair, args),
                UseDefaultPrefix = false,
                IsSubmenu = true,
                PrefixChar = 'P',
                PrefixColor = 530,
            });
        }

        private void DrawLifestreamInviteSubmenu(Pair pair, IMenuItemClickedArgs clickedArgs)
        {
            var menuItems = new List<MenuItem>();

            var folders = _ipcManager.Lifestream.GetAddressBookEntriesWithFolders();

            Logger.LogTrace("{service} Address Book Folders: {folders}",
                nameof(DrawLifestreamInviteSubmenu), folders);

            // get default book first - "Default Book" is Lifestream's Default Address book. Anything outside of this will show up in a folder. And technically Default Book is already a folder. We are just handling it first. 
            folders.TryGetValue("Default Book", out var defaultBookEntries);

            if (defaultBookEntries != null)
            {
                List<AddressBookEntry> sortedRoot = defaultBookEntries
                    .OrderBy(e => string.IsNullOrWhiteSpace(e.Name) ? 1 : 0)
                    .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(e => e.World)
                    .ToList();

                foreach (var entry in sortedRoot)
                {
                    var addressName = _ipcManager.Lifestream.GetAddressBookEntryTextWithName(entry);

                    menuItems.Add(new MenuItem()
                    {
                        Name = new SeStringBuilder().AddText(addressName).Build(),
                        OnClicked = _ => _apiController.SendLifestreamInviteToPair(pair, entry)
                    });
                }
            }

            // Folders
            foreach (var folder in folders.OrderBy(f => f.Key))
            {
                //skip default book, already done. 
                if (folder.Key.Equals("Default Book", StringComparison.OrdinalIgnoreCase)) continue;
                //skip if no folders
                if (folder.Value.Count == 0) continue;

                var folderItems = new List<MenuItem>();

                List<AddressBookEntry> sortedFolder = folder.Value
                    .OrderBy(e => string.IsNullOrWhiteSpace(e.Name) ? 1 : 0)
                    .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(e => e.World)
                    .ToList();

                foreach (var entry in sortedFolder)
                {
                    var addressName = _ipcManager.Lifestream.GetAddressBookEntryTextWithName(entry);

                    folderItems.Add(new MenuItem()
                    {
                        Name = new SeStringBuilder().AddText(addressName).Build(),
                        OnClicked = _ => _apiController.SendLifestreamInviteToPair(pair, entry)
                    });
                }

                folderItems.Add(new MenuItem()
                {
                    Name = new SeStringBuilder().AddText("Return").Build(),
                    IsReturn = true,
                    OnClicked = _ => _dalamudUtilService.OpenContextMenu(clickedArgs.AgentPtr)
                });

                menuItems.Add(new MenuItem()
                {
                    Name = new SeStringBuilder().AddText(folder.Key).Build(),
                    IsSubmenu = true,
                    OnClicked = args => args.OpenSubmenu(new SeStringBuilder().AddText(folder.Key).Build(),folderItems)
                });
            }

            menuItems.Add(new MenuItem()
            {
                Name = new SeStringBuilder().AddText("Use Current Location").Build(),
                OnClicked = _ => _apiController.SendLifestreamInviteToPair(pair)
            });

            menuItems.Add(new MenuItem()
            {
                Name = new SeStringBuilder().AddText("Return").Build(),
                IsReturn = true,
                OnClicked = _ => _dalamudUtilService.OpenContextMenu(clickedArgs.AgentPtr)
            });

            clickedArgs.OpenSubmenu(new SeStringBuilder().AddText("Lifestream Address Book").Build(), menuItems);
        }
    }
}