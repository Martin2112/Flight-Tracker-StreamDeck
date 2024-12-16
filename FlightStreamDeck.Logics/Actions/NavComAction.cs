﻿using FlightStreamDeck.Logics.Actions.NavCom;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SharpDeck.Enums;
using System;
using System.Linq;
using System.Timers;

namespace FlightStreamDeck.Logics.Actions;

public class NavComSettings
{
    public string Type { get; set; }
    public string HoldFunction { get; set; }
    public string AvionicsValue { get; set; }
    public string BattMasterValue { get; set; }
    [JsonProperty(nameof(ImageBackground))]
    public string ImageBackground { get; set; }
    [JsonProperty(nameof(ImageBackground_base64))]
    public string? ImageBackground_base64 { get; set; }
}

[StreamDeckAction("tech.flighttracker.streamdeck.generic.navcom")]
public class NavComAction : BaseAction<NavComSettings>, EmbedLinkLogic.IAction
{
    private const int HOLD_DURATION_MILLISECONDS = 1000;
    private const string minNavVal = "10800";
    private const string maxNavVal = "11795";
    private const string minComVal = "118000";
    private const string maxComVal = "136990";
    private const string minXpdrVal = "0000";
    private const string maxXpdrVal = "7777";

    private readonly ILogger<NavComAction> logger;
    private readonly IImageLogic imageLogic;
    private readonly IFlightConnector flightConnector;
    private readonly IEventRegistrar eventRegistrar;
    private readonly IEventDispatcher eventDispatcher;
    private readonly SimVarManager simVarManager;

    private readonly Timer timer;
    private readonly EmbedLinkLogic embedLinkLogic;

    NavComHandler? handler = null;

    string? lastValue1 = null;
    string? lastValue2 = null;
    bool lastDependant = false;

    private TaskCompletionSource<bool>? initializationTcs;

    public NavComAction(
        ILogger<NavComAction> logger,
        IImageLogic imageLogic,
        IFlightConnector flightConnector,
        IEventRegistrar eventRegistrar,
        IEventDispatcher eventDispatcher,
        SimVarManager simVarManager,
        RegistrationParameters registrationParameters
    ) : base(registrationParameters)
    {
        this.logger = logger;
        this.imageLogic = imageLogic;
        this.flightConnector = flightConnector;
        this.eventRegistrar = eventRegistrar;
        this.eventDispatcher = eventDispatcher;
        this.simVarManager = simVarManager;
        timer = new Timer { Interval = HOLD_DURATION_MILLISECONDS };
        timer.Elapsed += Timer_Elapsed;

        embedLinkLogic = new EmbedLinkLogic(this);
    }

    private async void Timer_Elapsed(object? sender, ElapsedEventArgs e)
    {
        timer.Stop();

        if (settings != null)
        {
            // Handle hold
            if (settings.HoldFunction != "Swap")
            {
                await SwitchToNumpad();
            }
            else
            {
                handler?.SwapFrequencies();
            }
        }
    }

    protected override async Task OnWillAppear(ActionEventArgs<AppearancePayload> args)
    {
        await base.OnWillAppear(args);

        flightConnector.GenericValuesUpdated += FlightConnector_GenericValuesUpdated;

        var settings = args.Payload.GetSettings<NavComSettings>();
        await InitializeSettingsAsync(settings);

        await UpdateImage(false, string.Empty, string.Empty, false);

        var tcs = initializationTcs;
        if (tcs != null)
        {
            logger.LogDebug("Trigger Task completion for initialization");
            tcs.SetResult(true);
        }
    }

    protected override Task OnWillDisappear(ActionEventArgs<AppearancePayload> args)
    {
        flightConnector.GenericValuesUpdated -= FlightConnector_GenericValuesUpdated;
        SwitchTo(null, null, null);

        return Task.CompletedTask;
    }

    protected override Task OnKeyDown(ActionEventArgs<KeyPayload> args)
    {
        if (lastDependant)
        {
            var device = registrationParameters.Info.Devices.FirstOrDefault(o => o.Id == args.Device);
            if (device != null && device.Type != DeviceType.StreamDeckMini)
            {
                this.device = device;
                timer.Start();
            }
        }
        return Task.CompletedTask;
    }

    protected override Task OnKeyUp(ActionEventArgs<KeyPayload> args)
    {
        if (lastDependant)
        {
            var device = registrationParameters.Info.Devices.FirstOrDefault(o => o.Id == args.Device);
            if (timer.Enabled || device?.Type == DeviceType.StreamDeckMini)
            {
                timer.Stop();

                // Click
                if (settings != null)
                {
                    if (settings.HoldFunction != "Swap")
                    {
                        handler?.SwapFrequencies();
                    }
                    else
                    {
                        return SwitchToNumpad();
                    }
                }
            }
        }
        return Task.CompletedTask;
    }

    protected override async Task OnSendToPlugin(ActionEventArgs<JObject> args)
    {

        if (args.Payload.TryGetValue("convertToEmbed", out JToken? fileKeyObject))
        {
            var fileKey = fileKeyObject.Value<string>();
            await embedLinkLogic.ConvertLinkToEmbedAsync(fileKey);
        }
        else if (args.Payload.TryGetValue("convertToLink", out fileKeyObject))
        {
            var fileKey = fileKeyObject.Value<string>();
            await embedLinkLogic.ConvertEmbedToLinkAsync(fileKey);
        }
        else
        {
            await InitializeSettingsAsync(args.Payload.ToObject<NavComSettings>());
        }

        await UpdateImage(false, string.Empty, string.Empty, false);
    }

    public override Task InitializeSettingsAsync(NavComSettings? settings)
    {
        this.settings = settings;
        if (settings == null) return Task.CompletedTask;

        lastDependant = !lastDependant;
        lastValue1 = null;
        lastValue2 = null;

        SwitchTo(
            settings.Type,
            settings.BattMasterValue,
            settings.AvionicsValue
        );

        return Task.CompletedTask;
    }

    private async void FlightConnector_GenericValuesUpdated(object? sender, ToggleValueUpdatedEventArgs e)
    {
        var settings = this.settings;

        if (settings != null)
        {
            if (handler != null)
            {
                var (value1, value2, showMainOnly, dependant) = handler.GetDisplayValues(e.GenericValueStatus);

                if (lastValue1 != value1 || lastValue2 != value2 || lastDependant != dependant)
                {
                    lastValue1 = value1;
                    lastValue2 = value2;
                    lastDependant = dependant;
                    await UpdateImage(dependant, value1, value2, showMainOnly);
                }
            }
        }
    }

    private async Task UpdateImage(bool dependant, string value1, string value2, bool showMainOnly)
    {
        if (settings != null)
        {
            await SetImageSafeAsync(imageLogic.GetNavComImage(settings.Type, dependant, value1, value2, showMainOnly, settings.ImageBackground, GetImageBytes(), device.IsHighResolution()));
        }
    }

    private byte[]? GetImageBytes() => settings?.ImageBackground_base64 != null ? Convert.FromBase64String(settings.ImageBackground_base64) : null;

    private void SwitchTo(string? type, string? batteryVariable, string? avionicsVariable)
    {
        handler?.DeRegisterSimValues();
        switch (type)
        {
            case "NAV1":
                handler = new BcdHandler(
                    eventRegistrar,
                    eventDispatcher,
                    simVarManager,
                    "NAV_ACTIVE_FREQUENCY__1",
                    "NAV_STANDBY_FREQUENCY__1",
                    batteryVariable,
                    avionicsVariable,
                    KnownEvents.NAV1_RADIO_SWAP,
                    KnownEvents.NAV1_STBY_SET,
                    minNavVal,
                    maxNavVal,
                    "108.00"
                );
                break;
            case "NAV2":
                handler = new BcdHandler(
                    eventRegistrar,
                    eventDispatcher,
                    simVarManager,
                    "NAV_ACTIVE_FREQUENCY__2",
                    "NAV_STANDBY_FREQUENCY__2",
                    batteryVariable,
                    avionicsVariable,
                    KnownEvents.NAV2_RADIO_SWAP,
                    KnownEvents.NAV2_STBY_SET,
                    minNavVal,
                    maxNavVal,
                    "108.00"
                );
                break;
            case "COM1":
                handler = new HzHandler(
                    eventRegistrar,
                    eventDispatcher,
                    simVarManager,
                    "COM_ACTIVE_FREQUENCY__1",
                    "COM_STANDBY_FREQUENCY__1",
                    batteryVariable,
                    avionicsVariable,
                    KnownEvents.COM_STBY_RADIO_SWAP,
                    KnownEvents.COM_STBY_RADIO_SET_HZ,
                    minComVal,
                    maxComVal,
                    "118.000"
                );
                break;
            case "COM2":
                handler = new HzHandler(
                    eventRegistrar,
                    eventDispatcher,
                    simVarManager,
                    "COM_ACTIVE_FREQUENCY__2",
                    "COM_STANDBY_FREQUENCY__2",
                    batteryVariable,
                    avionicsVariable,
                    KnownEvents.COM2_RADIO_SWAP,
                    KnownEvents.COM2_STBY_RADIO_SET_HZ,
                    minComVal,
                    maxComVal,
                    "118.000"
                );
                break;
            case "XPDR":
                handler = new XpdrHandler(
                    eventRegistrar,
                    eventDispatcher,
                    simVarManager,
                    "TRANSPONDER_CODE__1",
                    null,
                    batteryVariable,
                    avionicsVariable,
                    null,
                    KnownEvents.XPNDR_SET,
                    minXpdrVal,
                    maxXpdrVal,
                    "1200"
                );
                break;
            case "ADF1":
                handler = new AdfHandler(
                    eventRegistrar,
                    eventDispatcher,
                    simVarManager,
                    "ADF_ACTIVE_FREQUENCY__1",
                    "ADF_STANDBY_FREQUENCY__1",
                    batteryVariable,
                    avionicsVariable,
                    KnownEvents.ADF1_RADIO_SWAP,
                    KnownEvents.ADF_STBY_SET
                );
                break;
            case "ADF2":
                handler = new AdfHandler(
                    eventRegistrar,
                    eventDispatcher,
                    simVarManager,
                    "ADF_ACTIVE_FREQUENCY__2",
                    "ADF_STANDBY_FREQUENCY__2",
                    batteryVariable,
                    avionicsVariable,
                    KnownEvents.ADF2_RADIO_SWAP,
                    KnownEvents.ADF2_STBY_SET
                );
                break;
            default:
                handler = null;
                break;
        }
        handler?.RegisterSimValuesAndEvents();
    }

    private async Task SwitchToNumpad()
    {
        var handler = this.handler;
        if (settings?.Type != null && handler?.IsSettable == true && lastDependant && device != null)
        {
            if (device.Type == (DeviceType)7 && settings.Type != "COM1" && settings.Type != "COM1" && settings.Type != "NAV1" && settings.Type != "NAV2")
            {
                // Support only COM/NAV for SD+
                return;
            }

            NumpadStorage.NumpadParams = new NumpadParams(
                settings.Type,
                handler.MinPattern,
                handler.MaxPattern,
                handler.Mask,
                lastValue2,
                settings.ImageBackground,
                GetImageBytes()
            );
            NumpadStorage.NumpadTcs = new TaskCompletionSource<(string?, bool)>();

            this.initializationTcs = new TaskCompletionSource<bool>();

            await StreamDeck.SwitchToProfileAsync(registrationParameters.PluginUUID,
                device.Id,
                device.Type switch
                {
                    DeviceType.StreamDeckXL => "Profiles/Numpad_XL",
                    (DeviceType)7 => "Profiles/Numpad_Plus",
                    _ => "Profiles/Numpad"
                });

            try
            {
                await initializationTcs.Task;
            }
            finally
            {
                initializationTcs = null;
            }

            var (value, swap) = await NumpadStorage.NumpadTcs.Task;
            if (handler != null && !string.IsNullOrEmpty(value))
            {
                await handler.TriggerAsync(value, swap);
            }
        }
    }

    public string? GetImagePath(string fileKey) => fileKey switch
    {
        "ImageBackground" => settings?.ImageBackground,
        _ => throw new ArgumentOutOfRangeException(nameof(fileKey), $"'{fileKey}' is invalid.")
    };

    public string? GetImageBase64(string fileKey) => fileKey switch
    {
        "ImageBackground" => settings?.ImageBackground_base64,
        _ => throw new ArgumentOutOfRangeException(nameof(fileKey), $"'{fileKey}' is invalid.")
    };

    public void SetImagePath(string fileKey, string path)
    {
        if (settings != null)
        {
            switch (fileKey)
            {
                case "ImageBackground": settings.ImageBackground = path; break;
                default: throw new ArgumentOutOfRangeException(nameof(fileKey), $"'{fileKey}' is invalid.");
            }
        }
    }

    public void SetImageBase64(string fileKey, string? base64)
    {
        if (settings != null)
        {
            switch (fileKey)
            {
                case "ImageBackground": settings.ImageBackground_base64 = base64; break;
                default: throw new ArgumentOutOfRangeException(nameof(fileKey), $"'{fileKey}' is invalid.");
            }
        }
    }
}
