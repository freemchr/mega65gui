namespace Mega65Gui.Core.Device;

/// <summary>Plain-language explanations for known failure messages from the official tools.</summary>
public static class ToolFailureHints
{
    /// <summary>
    /// Returns an explanation for a failed run, or null when the output has no known cause.
    /// m65 -q needs the JTAG side of the adapter (FTDI interface 0) on the WinUSB driver. Without
    /// it m65 cannot read the FPGA's ID and then wrongly reports that the bitstream is for a
    /// different model.
    /// </summary>
    public static string? Explain(string output) =>
        output.Contains("needs to be WinUSB", StringComparison.OrdinalIgnoreCase)
            ? "Loading a bitstream needs the WinUSB driver on the adapter's JTAG interface (\"Serial Converter A\" / Interface 0). " +
              "Windows normally installs a different driver there, so m65 cannot reach the FPGA and reports the wrong model. " +
              "Nothing was changed on the MEGA65. Install WinUSB on Interface 0 only (for example with Zadig); leave Interface 1, the COM port, as it is."
            : null;
}
