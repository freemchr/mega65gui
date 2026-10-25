namespace Mega65Gui.Core.Update;

public enum PreflightAction
{
    /// <summary>Everything the update needs is there.</summary>
    Go,
    /// <summary>Connect to the MEGA65 over USB/JTAG first, then check again.</summary>
    Connect,
    /// <summary>Stop and show the message.</summary>
    Stop,
}

public sealed record PreflightResult(PreflightAction Action, string? Message = null);

/// <summary>What Update My MEGA65 has to set up before it can run.</summary>
/// <param name="CardInDevice">The card is inside the MEGA65 (false: in a PC card reader).</param>
/// <param name="ModelChosen">A model is picked (false: "Auto", read from the MEGA65).</param>
/// <param name="Connected">A MEGA65 is connected and answering.</param>
/// <param name="ConnectedOverUsb">That connection is USB/JTAG, which can read the model (Ethernet cannot).</param>
/// <param name="ToolsInstalled">The official device tools are installed.</param>
/// <param name="CardFolderExists">The chosen SD card folder exists (card in a PC reader).</param>
public sealed record UpdatePreflight(bool CardInDevice, bool ModelChosen, bool Connected, bool ConnectedOverUsb, bool ToolsInstalled, bool CardFolderExists)
{
    public const string ChooseModel = "Choose your MEGA65 model. \"Auto\" can only read it from a MEGA65 connected by the USB cable.";

    public PreflightResult Check()
    {
        var canReadModel = Connected && ConnectedOverUsb;
        if (CardInDevice)
        {
            // The card inside the MEGA65 is reached over the connection.
            if (!Connected) return new(PreflightAction.Connect);
            if (!ToolsInstalled) return new(PreflightAction.Stop, "Updating the card inside the MEGA65 needs the device tools. Install them from the sidebar.");
            if (!ModelChosen && !canReadModel) return new(PreflightAction.Stop, "Choose your MEGA65 model: it cannot be read over Ethernet.");
            return new(PreflightAction.Go);
        }
        // A card in a PC reader usually means the MEGA65 is not connected: ask for the model, never a COM port.
        if (!ModelChosen && !canReadModel) return new(PreflightAction.Stop, ChooseModel);
        if (!CardFolderExists) return new(PreflightAction.Stop, "Choose the SD card folder first.");
        return new(PreflightAction.Go);
    }
}
