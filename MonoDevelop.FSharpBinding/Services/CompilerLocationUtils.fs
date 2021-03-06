namespace MonoDevelop.FSharp

open System
open System.IO
open System.Configuration
open System.Reflection
open Microsoft.Win32
open MonoDevelop.Core.Assemblies
open System.Runtime.InteropServices
open System.Text.RegularExpressions

#nowarn "44" // ConfigurationSettings is obsolete but the new stuff is horribly complicated. 

type FSharpCompilerVersion = 
    // F# 2.0
    | FSharp_2_0 
    // F# 3.0
    | FSharp_3_0
    override x.ToString() = match x with | FSharp_2_0 -> "4.0.0.0" | FSharp_3_0 -> "4.3.0.0"
    /// The current requested language version is a configuration setting specified by the user.
    static member CurrentRequestedVersion 
        with get() = 
            let setting = MonoDevelop.Core.PropertyService.Get<string>("FSharpBinding.EnableFSharp30","") 
            if System.String.Compare(setting, "true", true) = 0 then 
                FSharpCompilerVersion.FSharp_3_0
            else
                FSharpCompilerVersion.FSharp_2_0
                

module internal FSharpEnvironment =

  let FSharpCoreLibRunningVersion =
    try 
      match (typeof<Microsoft.FSharp.Collections.List<int>>).Assembly.GetName().Version.ToString() with
      | null -> None
      | "" -> None
      | s  -> Some(s)
    with _ -> None

  // Returns:
  // -- on 2.0:  "v2.0.50727"
  // -- on 4.0:  "v4.0.30109" (last 5 digits vary by build)
  let MSCorLibRunningRuntimeVersion = 
    typeof<int>.Assembly.ImageRuntimeVersion

  // The F# team version number. This version number is used for
  //   - the F# version number reported by the fsc.exe and fsi.exe banners in the CTP release
  //   - the F# version number printed in the HTML documentation generator
  //   - the .NET DLL version number for all VS2008 DLLs
  //   - the VS2008 registry key, written by the VS2008 installer
  //     HKEY_LOCAL_MACHINE\Software\Microsoft\.NETFramework\AssemblyFolders\Microsoft.FSharp-" + FSharpTeamVersionNumber
  // Also
  //   - for Beta2, the language revision number indicated on the F# language spec
  //
  // It is NOT the version number listed on FSharp.Core.dll
  let FSharpTeamVersionNumber = "2.0.0.0"

  // The F# binary format revision number. The first three digits of this form the significant part of the 
  // format revision number for F# binary signature and optimization metadata. The last digit is not significant.
  //
  // WARNING: Do not change this revision number unless you absolutely know what you're doing.
  let FSharpBinaryMetadataFormatRevision = "2.0.0.0"

  [<DllImport("Advapi32.dll", CharSet = CharSet.Unicode, BestFitMapping = false)>]
  extern uint32 RegOpenKeyExW(UIntPtr _hKey, string _lpSubKey, uint32 _ulOptions, int _samDesired, UIntPtr & _phkResult);

  [<DllImport("Advapi32.dll", CharSet = CharSet.Unicode, BestFitMapping = false)>]
  extern uint32 RegQueryValueExW(UIntPtr _hKey, string _lpValueName, uint32 _lpReserved, uint32 & _lpType, IntPtr _lpData, int & _lpchData);

  [<DllImport("Advapi32.dll")>]
  extern uint32 RegCloseKey(UIntPtr _hKey)

  module Option = 
    /// Convert string into Option string where null and String.Empty result in None
    let ofString s = 
      if String.IsNullOrEmpty(s) then None
      else Some(s)

      
    

  // MaxPath accounts for the null-terminating character, for example, the maximum path on the D drive is "D:\<256 chars>\0". 
  // See: ndp\clr\src\BCL\System\IO\Path.cs
  let maxPath = 260;
  let maxDataLength = (new System.Text.UTF32Encoding()).GetMaxByteCount(maxPath)
  let KEY_WOW64_DEFAULT = 0x0000
  let KEY_WOW64_32KEY = 0x0200
  let HKEY_LOCAL_MACHINE = UIntPtr(0x80000002u)
  let KEY_QUERY_VALUE = 0x1
  let REG_SZ = 1u

  let GetDefaultRegistryStringValueViaDotNet(subKey: string)  =
    Option.ofString
      (try
        downcast Microsoft.Win32.Registry.GetValue("HKEY_LOCAL_MACHINE\\"+subKey,null,null)
       with e->
        System.Diagnostics.Debug.Assert(false, sprintf "Failed in GetDefaultRegistryStringValueViaDotNet: %s" (e.ToString()))
        null)

  let Get32BitRegistryStringValueViaPInvoke(subKey:string) = 
    Option.ofString
      (try
        // 64 bit flag is not available <= Win2k
        let options =
          match Environment.OSVersion.Version.Major with
            | major when major >= 5 -> KEY_WOW64_32KEY
            | _ -> KEY_WOW64_DEFAULT


        let mutable hkey = UIntPtr.Zero;
        let pathResult = Marshal.AllocCoTaskMem(maxDataLength);

        try
          let res = RegOpenKeyExW(HKEY_LOCAL_MACHINE,subKey, 0u, KEY_QUERY_VALUE ||| options, & hkey)
          if res = 0u then
            let mutable uType = REG_SZ;
            let mutable cbData = maxDataLength;

            let res = RegQueryValueExW(hkey, null, 0u, &uType, pathResult, &cbData);

            if (res = 0u && cbData > 0 && cbData <= maxDataLength) then
              Marshal.PtrToStringUni(pathResult, (cbData - 2)/2);
            else
              null
          else
            null
        finally
          if hkey <> UIntPtr.Zero then
            RegCloseKey(hkey) |> ignore
        
          if pathResult <> IntPtr.Zero then
            Marshal.FreeCoTaskMem(pathResult)
       with e->
        System.Diagnostics.Debug.Assert(false, sprintf "Failed in Get32BitRegistryStringValueViaPInvoke: %s" (e.ToString()))
        null)

  let is32Bit = IntPtr.Size = 4
  
  let tryRegKey(subKey:string) = 

    if is32Bit then
      let s = GetDefaultRegistryStringValueViaDotNet(subKey)
      // If we got here AND we're on a 32-bit OS then we can validate that Get32BitRegistryStringValueViaPInvoke(...) works
      // by comparing against the result from GetDefaultRegistryStringValueViaDotNet(...)
#if DEBUG
      let viaPinvoke = Get32BitRegistryStringValueViaPInvoke(subKey)
      System.Diagnostics.Debug.Assert((s = viaPinvoke), sprintf "32bit path: pi=%A def=%A" viaPinvoke s)
#endif
      s
    else
      Get32BitRegistryStringValueViaPInvoke(subKey) 

  let internal tryCurrentDomain() = 
    let pathFromCurrentDomain = System.AppDomain.CurrentDomain.BaseDirectory
    if not(String.IsNullOrEmpty(pathFromCurrentDomain)) then 
      Some pathFromCurrentDomain
    else
      None
  
  let internal tryAppConfig (appConfigKey:string) = 

    let locationFromAppConfig = ConfigurationSettings.AppSettings.[appConfigKey]
    System.Diagnostics.Debug.Print(sprintf "Considering appConfigKey %s which has value '%s'" appConfigKey locationFromAppConfig) 

    if String.IsNullOrEmpty(locationFromAppConfig) then 
      None
    else
      let exeAssemblyFolder = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)
      let locationFromAppConfig = locationFromAppConfig.Replace("{exepath}", exeAssemblyFolder)
      System.Diagnostics.Debug.Print(sprintf "Using path %s" locationFromAppConfig) 
      Some locationFromAppConfig

  /// Try to find the F# compiler location by looking at the "fsharpi" script installed by F# packages
  let internal tryFsharpiScript(url:string) =
    try
      let str = File.ReadAllText(url)
      let reg = new Regex("mono (\/.*)\/fsi\.exe")
      let res = reg.Match(str)
      if res.Success then Some(res.Groups.[1].Value) else None
    with e -> 
      None


  let BackupInstallationProbePoints = 
      [ // prefer the latest installation of Mono on Mac
        "/Library/Frameworks/Mono.framework/Versions/Current"
        // prefer freshly built F# compilers on Linux
        "/usr/local"
        // otherwise look in the standard place
        "/usr" ]

    // The default location of FSharp.Core.dll and fsc.exe based on the version of fsc.exe that is running
  // Used for
  //   - location of design-time copies of FSharp.Core.dll and FSharp.Compiler.Interactive.Settings.dll for the default assumed environment for scripts
  //   - default ToolPath in tasks in FSharp.Build.dll (for Fsc tasks)
  //   - default F# binaries directory in service.fs (REVIEW: check this)
  //   - default location of fsi.exe in FSharp.VS.FSI.dll
  //   - default location of fsc.exe in FSharp.Compiler.CodeDom.dll
  let BinFolderOfDefaultFSharpCompiler() = 
    // Check for an app.config setting to redirect the default compiler location
    // Like fsharp-compiler-location
    try 
      let result = tryAppConfig "fsharp-compiler-location"
      match result with 
      | Some _ ->  result 
      | None -> 

        // Note: If the keys below change, be sure to update code in:
        // Property pages (ApplicationPropPage.vb)

        let key20 = @"Software\Microsoft\.NETFramework\AssemblyFolders\Microsoft.FSharp-" + FSharpTeamVersionNumber 
        let key40 = 
            match FSharpCompilerVersion.CurrentRequestedVersion with 
            | FSharp_2_0 ->  @"Software\Microsoft\FSharp\2.0\Runtime\v4.0"
            | FSharp_3_0 ->  @"Software\Microsoft\FSharp\3.0\Runtime\v4.0"
        let key1,key2 = 
          match FSharpCoreLibRunningVersion with 
          | None -> key20,key40 
          | Some v -> if v.Length > 1 && v.[0] <= '3' then key20,key40 else key40,key20
        
        let result = tryRegKey key1
        match result with 
        | Some _ ->  result 
        | None -> 
        let result =  tryRegKey key2
        match result with 
        | Some _ ->  result 
        | None ->
        let result = 
            let var = System.Environment.GetEnvironmentVariable("FSHARP_COMPILER_BIN")
            if String.IsNullOrEmpty(var) then None
            else Some(var)
        match result with 
        | Some _ -> result
        | None -> 
        // NOTE: we should probably probe the path here??
        let result = 
            BackupInstallationProbePoints |> List.tryPick (fun x -> 
               let safeExists f = (try File.Exists(f) with _ -> false)
               let file f = Path.Combine(Path.Combine(x,"bin"),f)
               let exists f = safeExists(file f)
               if exists "fsc" && exists "fsi" then tryFsharpiScript (file "fsi")
               elif exists "fsharpc" && exists "fsharpi" then tryFsharpiScript (file "fsharpi")
               else None)
                
        match result with 
        | Some _ -> result
        | None -> 
        None
    with e -> 
      System.Diagnostics.Debug.Assert(false, "Error while determining default location of F# compiler")
      None


  let FolderOfDefaultFSharpCore(targetFramework) = 
    try 
      let result = tryAppConfig "fsharp-core-location"
      match result with 
      | Some _ ->  result 
      | None -> 

        // On Windows, look for the registry key giving the installation location of FSharp.Core.dll.
        // This only works for .NET 2.0 - 4.0. To target Silverlight or Portable you'll need to use a direct reference to
        // the right FSharp.Core.dll.
        let result =
            match FSharpCompilerVersion.CurrentRequestedVersion, targetFramework with 
            | FSharp_2_0, x when (x = TargetFrameworkMoniker.NET_2_0 || x = TargetFrameworkMoniker.NET_3_0 || x = TargetFrameworkMoniker.NET_3_5) -> 
                tryRegKey @"Software\Microsoft\.NETFramework\v2.0.50727\AssemblyFoldersEx\Microsoft Visual F# 4.0"
            | FSharp_2_0, _ -> 
                tryRegKey @"Software\Microsoft\.NETFramework\v4.0.30319\AssemblyFoldersEx\Microsoft Visual F# 4.0"
            | FSharp_3_0, x when (x = TargetFrameworkMoniker.NET_2_0 || x = TargetFrameworkMoniker.NET_3_0 || x = TargetFrameworkMoniker.NET_3_5) -> 
                tryRegKey @"Software\Microsoft\.NETFramework\v2.0.50727\AssemblyFoldersEx\F# 3.0 Core Assemblies"
            | FSharp_3_0, _ -> 
                tryRegKey @"Software\Microsoft\.NETFramework\v4.0.30319\AssemblyFoldersEx\F# 3.0 Core Assemblies"
            | _ -> None 
        
        match result with 
        | Some _ ->  result 
        | None -> 
        let result = 
            let var = System.Environment.GetEnvironmentVariable("FSHARP_CORE_LOCATION")
            if String.IsNullOrEmpty(var) then None
            else Some(var)
        match result with 
        | Some _ -> result
        | None -> 
        let possibleInstallationPoints = 
            Option.toList (BinFolderOfDefaultFSharpCompiler() |> Option.map Path.GetDirectoryName) @  
            BackupInstallationProbePoints
        Debug.tracef "Resolution" "targetFramework = %A" targetFramework
        let ext = 
            match targetFramework with 
            | x when (x = TargetFrameworkMoniker.NET_2_0 || x = TargetFrameworkMoniker.NET_3_0 || x = TargetFrameworkMoniker.NET_3_5) -> 
                "2.0"
            | _ -> 
                "4.0"
        let safeExists f = (try File.Exists(f) with _ -> false)
        let result = 
            possibleInstallationPoints |> List.tryPick (fun possibleInstallationDir -> 
 
              Debug.tracef "Resolution" "Probing for %s/lib/mono/%s/FSharp.Core.dll" possibleInstallationDir ext 
              let (++) s x = Path.Combine(s,x)
              let candidate = possibleInstallationDir ++ "lib" ++ "mono" ++ ext
              if safeExists (candidate ++ "FSharp.Core.dll") then 
                  Some candidate
              else
                  None)
                
        match result with 
        | Some _ -> result
        | None -> 
        let result = 
            possibleInstallationPoints |> List.tryPick (fun possibleInstallationDir -> 

                  Debug.tracef "Resolution" "Probing %s/bin for fsc/fsi scripts or fsharpc/fsharpi scripts" possibleInstallationDir
              
                  let file f = Path.Combine(Path.Combine(possibleInstallationDir,"bin"),f)
                  let exists f = safeExists(file f)
                  if exists "fsc" && exists "fsi" then tryFsharpiScript (file "fsi")
                  elif exists "fsharpc" && exists "fsharpi" then tryFsharpiScript (file "fsharpi")
                  else None)
                
        match result with 
        | Some _ -> result
        | None -> 
        None
    with e -> 
      System.Diagnostics.Debug.Assert(false, "Error while determining default location of F# compiler")
      None


