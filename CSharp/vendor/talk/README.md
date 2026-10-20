# Talk SDK packages

These internal packages were built from the user-provided `pod-chat-dotnet-sdk-0.10.6.22`
directory, using its `POD_Chat` and `POD_Async` library sources. The SDK declares
versions 0.10.6.22 and 0.9.1.12 respectively; neither version is available on the
public NuGet feed. `CSharp/NuGet.Config` includes this local package directory so
normal builds and Docker builds do not depend on a Downloads directory.

The `-maf.1` suffix distinguishes these builds from previously cached SDK packages.
No SDK C# source was changed. Project metadata was replaced to remove a legacy
machine-specific assembly reference and reference the supplied async library.
Dependencies were updated to Newtonsoft.Json 13.0.4, NLog 4.7.15, RestSharp
106.15.0, System.ComponentModel.Annotations 5.0.0 and System.Drawing.Common 8.0.21.
Apache.NMS.ActiveMQ 2.1.1 and WebSocket4Net 0.15.2 retain the SDK versions.
Only text messaging over the SDK queue transport is used by MAF.

To rebuild from the original SDK folder, run from `CSharp/`:

```powershell
./vendor/talk/build-sdk.ps1 -SdkRoot 'C:\path\to\pod-chat-dotnet-sdk-0.10.6.22'
```

The script leaves its copied build source in a unique temporary directory for
inspection. Packages contain library assemblies, without demos, configuration or
credentials. Keep these company-provided SDK packages internal.
