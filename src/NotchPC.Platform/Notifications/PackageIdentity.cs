using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32;
using Windows.Management.Deployment;

namespace NotchPC.Platform.Notifications;

/// <summary>
/// "Package identity" for the normal NotchPC.exe (a sparse package: an AppxManifest that points at the folder with
/// the exe). Windows only sends live notification events to apps with identity. Registered without a certificate
/// and without UAC through Windows Developer Mode; NotchPC.exe carries the matching &lt;msix&gt; element in its
/// manifest, so it gets the identity whenever it starts while the package is registered (from any folder).
/// Removing the package changes nothing else: the app keeps working without live notifications.
/// </summary>
public static class PackageIdentity
{
    // Must match the <msix> element in NotchPC.App/app.manifest.
    public const string PackageName = "NotchPC";
    public const string Publisher = "CN=NotchPC";
    public const string ApplicationId = "NotchPC";

    /// <summary>True if this process runs with the NotchPC package identity.</summary>
    public static bool HasIdentity
    {
        get
        {
            try
            {
                return Windows.ApplicationModel.Package.Current?.Id?.Name == PackageName;
            }
            catch (InvalidOperationException)
            {
                return false; // no identity
            }
            catch (COMException)
            {
                return false;
            }
        }
    }

    /// <summary>Windows Settings → System → For developers → Developer Mode.</summary>
    public static bool IsDeveloperModeOn()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock");
        return key?.GetValue("AllowDevelopmentWithoutDevLicense") is int on && on != 0;
    }

    public static bool IsRegistered()
    {
        try
        {
            return new PackageManager().FindPackagesForUser("", PackageName, Publisher).Any();
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Writes the manifest into <paramref name="packageDir"/> (the logo PNGs must already be in its Assets folder)
    /// and registers it for the current user with the exe folder as external location.
    /// Returns null on success, otherwise Windows' error text.
    /// </summary>
    public static async Task<string?> RegisterAsync(string packageDir, string exeDir, string exeName)
    {
        Directory.CreateDirectory(packageDir);
        var manifest = Path.Combine(packageDir, "AppxManifest.xml");
        await File.WriteAllTextAsync(manifest, ManifestXml(exeName), new UTF8Encoding(false)).ConfigureAwait(false);
        try
        {
            var options = new RegisterPackageOptions
            {
                ExternalLocationUri = new Uri(exeDir.EndsWith('\\') ? exeDir : exeDir + "\\"),
                DeveloperMode = true,
                ForceAppShutdown = false,
            };
            var result = await new PackageManager().RegisterPackageByUriAsync(new Uri(manifest), options);
            return result.IsRegistered ? null : (string.IsNullOrWhiteSpace(result.ErrorText) ? $"0x{result.ExtendedErrorCode?.HResult:X8}" : result.ErrorText);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or ArgumentException)
        {
            return ex.Message;
        }
    }

    /// <summary>Removes the package for the current user. Returns null on success, otherwise the error text.</summary>
    public static async Task<string?> UnregisterAsync()
    {
        try
        {
            var pm = new PackageManager();
            foreach (var p in pm.FindPackagesForUser("", PackageName, Publisher).ToList())
            {
                var result = await pm.RemovePackageAsync(p.Id.FullName);
                if (result.ExtendedErrorCode is not null)
                {
                    return result.ErrorText;
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    internal static string ManifestXml(string exeName) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
                 xmlns:uap3="http://schemas.microsoft.com/appx/manifest/uap/windows10/3"
                 xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
                 xmlns:desktop6="http://schemas.microsoft.com/appx/manifest/desktop/windows10/6"
                 xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
                 IgnorableNamespaces="uap uap3 uap10 desktop6 rescap">
          <Identity Name="{PackageName}" Publisher="{Publisher}" Version="1.0.0.0" ProcessorArchitecture="neutral" />
          <Properties>
            <DisplayName>NotchPC</DisplayName>
            <PublisherDisplayName>NotchPC</PublisherDisplayName>
            <Logo>Assets\StoreLogo.png</Logo>
            <uap10:AllowExternalContent>true</uap10:AllowExternalContent>
            <desktop6:FileSystemWriteVirtualization>disabled</desktop6:FileSystemWriteVirtualization>
            <desktop6:RegistryWriteVirtualization>disabled</desktop6:RegistryWriteVirtualization>
          </Properties>
          <Resources>
            <Resource Language="en-us" />
          </Resources>
          <Dependencies>
            <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.26100.0" />
          </Dependencies>
          <Capabilities>
            <rescap:Capability Name="runFullTrust" />
            <rescap:Capability Name="unvirtualizedResources" />
            <uap3:Capability Name="userNotificationListener" />
          </Capabilities>
          <Applications>
            <Application Id="{ApplicationId}" Executable="{SecurityElement.Escape(exeName)}" uap10:TrustLevel="mediumIL" uap10:RuntimeBehavior="win32App">
              <uap:VisualElements DisplayName="NotchPC" Description="NotchPC" BackgroundColor="transparent"
                                  Square150x150Logo="Assets\Square150.png" Square44x44Logo="Assets\Square44.png" AppListEntry="none" />
            </Application>
          </Applications>
        </Package>
        """;
}
