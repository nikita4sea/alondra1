using Alondra1.Core.Config;

namespace Alondra1.Core.Mail;

/// <summary>Reader decisions that do not depend on the UI: which format opens, and whether remote images load.</summary>
public static class ReaderPolicy
{
    /// <summary>
    /// Remote images load when the setting is Always, when the sender is trusted, or when the user chose
    /// "Load images" for this message. There is no image proxy, so loading reveals this PC's address to the image host.
    /// </summary>
    public static bool AllowRemoteImages(ReadingSettings settings, string? senderAddress, bool loadedForMessage) =>
        loadedForMessage
        || settings.RemoteImages == RemoteImagePolicy.Always
        || IsTrusted(settings, senderAddress);

    public static bool IsTrusted(ReadingSettings settings, string? senderAddress) =>
        !string.IsNullOrWhiteSpace(senderAddress)
        && settings.TrustedSenders.Any(item => string.Equals(item, senderAddress.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds a sender to the trusted list (lower case, once). Returns false when it was already there or invalid.</summary>
    public static bool Trust(ReadingSettings settings, string? senderAddress)
    {
        var address = senderAddress?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(address) || !address.Contains('@') || address.Contains(' ') || IsTrusted(settings, address))
            return false;
        settings.TrustedSenders.Add(address);
        return true;
    }

    /// <summary>HTML opens by default when the message has it and the user did not choose plain text.</summary>
    public static bool OpenAsHtml(ReadingSettings settings, bool hasHtml, bool? chosenForMessage) =>
        hasHtml && (chosenForMessage ?? settings.PreferHtml);

    /// <summary>File types that run code when opened; opening them asks first.</summary>
    public static bool IsRisky(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension is ".exe" or ".msi" or ".bat" or ".cmd" or ".com" or ".scr" or ".pif" or ".vbs" or ".vbe" or ".js" or ".jse"
            or ".wsf" or ".wsh" or ".ps1" or ".psm1" or ".hta" or ".cpl" or ".msc" or ".jar" or ".lnk" or ".reg" or ".dll" or ".appx"
            or ".msix" or ".iso" or ".img" or ".vhd" or ".vhdx" or ".docm" or ".xlsm" or ".pptm" or ".url" or ".scf" or ".chm";
    }
}
