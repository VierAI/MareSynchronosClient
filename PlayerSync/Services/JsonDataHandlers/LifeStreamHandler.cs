using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using MareSynchronos.API.Data.AdditionalTypes;
using MareSynchronos.API.Data.Enum;
using MareSynchronos.API.Data.Validators;
using MareSynchronos.API.Dto;
using MareSynchronos.Interop.Ipc;
using MareSynchronos.MareConfiguration;
using MareSynchronos.PlayerData.Pairs;
using MareSynchronos.Services;
using MareSynchronos.Services.Mediator;
using MareSynchronos.WebAPI;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;


namespace PlayerSync.Services
{
    public class LifeStreamHandler : DisposableMediatorSubscriberBase
    {
        private readonly DalamudUtilService _dalamudUtilService;
        private readonly MareConfigService _configurationService;
        private readonly PairManager _pairManager;
        private readonly ApiController _apiController;
        private readonly IpcManager _ipcManager;
        private readonly IChatGui _chatGui;

        private readonly ConcurrentDictionary<string, AddressBookEntry> _addressCache = new(StringComparer.OrdinalIgnoreCase);
        DalamudLinkPayload? _lifestreamInvite = null;

        public LifeStreamHandler(ILogger logger, MareMediator mediator, 
            MareConfigService mareConfigService, DalamudUtilService dalamudUtilService, PairManager pairManager,
            ApiController apiController, IpcManager ipcManager, IChatGui chatGui) : base(logger, mediator)
        {
            _dalamudUtilService = dalamudUtilService;
            _configurationService = mareConfigService;
            _pairManager = pairManager;
            _apiController = apiController;
            _ipcManager = ipcManager;
            _chatGui = chatGui;

            _lifestreamInvite = _chatGui.AddChatLinkHandler(1, (_, a) => LifestreamInviteIpcHandler(a));
        }

        public void LifestreamLocationInviteHandler(JsonDataTypeDto dto)
        {
            if (!_ipcManager.Lifestream.APIAvailable) return;

            if (!JsonDataTypeValidators.TryValidate<LifestreamParseableAddress>(dto.JsonDataType, dto.JsonData, out var address)) return;

            if (address == null) return;

            var pair = _pairManager.GetPairByUID(dto.UserData.UID);
            if (pair == null) return;

            if (_configurationService.Current.LifestreamInvitesDirectPairsOnly && pair.IndividualPairStatus != IndividualPairStatus.Bidirectional) return;

            var addressBookEntryDto = address.AddressBookEntry;
            AddressBookEntry addressBookEntry = (
                    addressBookEntryDto.Name,
                    addressBookEntryDto.World,
                    addressBookEntryDto.City,
                    addressBookEntryDto.Ward,
                    addressBookEntryDto.PropertyType,
                    addressBookEntryDto.Plot,
                    addressBookEntryDto.Apartment,
                    addressBookEntryDto.ApartmentSubdivision,
                    addressBookEntryDto.AliasEnabled,
                    addressBookEntryDto.Alias
                );

            string psync = "[PlayerSync] ";
            string invite = string.IsNullOrWhiteSpace(pair.PlayerName) ? $"UID/Alias {pair.UserData.AliasOrUID}" : $"Player {pair.PlayerName}";

            string destination = _ipcManager.Lifestream.GetAddressBookEntryTextWithName(addressBookEntry);

            if (addressBookEntryDto.PropertyType == 1 && addressBookEntryDto.ApartmentSubdivision)
                destination += " (Subdivision)";

            Logger.LogTrace("Address book entry: {entry}", addressBookEntry);

            if (!_addressCache.TryAdd(NormalizeAddressCacheKey(destination), addressBookEntry))
                Logger.LogDebug("Failed to add address book entry to cache. {address} {entry}", destination, addressBookEntry);

            Logger.LogTrace("Address parse: {1}", destination);

            // 1.15.5.1417: the inviter's name is a player link (PlayerPayload with home world) when they are in the object table.
            var linkName = pair.PlayerName;
            var linkIdent = pair.HashedCID;
            _ = _dalamudUtilService.RunOnFrameworkThread(() =>
            {
                uint world = 0;
                try
                {
                    if (!string.IsNullOrWhiteSpace(linkName)) world = _dalamudUtilService.GetHomeWorldIdOnFramework(pair.ContendId, linkIdent, linkName);
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Lifestream invite: home world lookup failed");
                }

                SeStringBuilder se = new SeStringBuilder();
                if (world != 0)
                    se.AddText(psync + "Player ").Add(new PlayerPayload(linkName!, world));
                else
                    se.AddText(psync + invite);

                se
                .AddText(" has sent you a Lifestream invite. Click to travel to: ")
                .Add(_lifestreamInvite!)
                .AddUiForeground(destination, 37)
                .AddUiForegroundOff()
                .Add(RawPayload.LinkTerminator);

                _chatGui.Print(se.Build());
            });
        }

        private void LifestreamInviteIpcHandler(SeString address)
        {
            Logger.LogTrace("LIFESTREAM ADDRESS: {1}", address.TextValue);

            string addressCache = address.TextValue;

            if (!_addressCache.TryGetValue(NormalizeAddressCacheKey(addressCache), out var addressBookEntry))
                Logger.LogWarning("Could not find valid address for {address}", address.TextValue);

            if (!_ipcManager.Lifestream.TryGoToHousingAddress(addressBookEntry))
                Logger.LogWarning("Error in Lifestream command execution for {address}", address.TextValue);
        }

        private static string NormalizeAddressCacheKey(string value)
        {
            return string.Join(" ", value.Replace('\u00A0', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            _lifestreamInvite = null;
        }
    }
}
