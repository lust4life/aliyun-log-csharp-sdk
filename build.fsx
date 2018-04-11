#r @"packages/build/FAKE/tools/FakeLib.dll"
open Fake
open System
open System.IO

Environment.CurrentDirectory <- __SOURCE_DIRECTORY__

let release = IO.File.ReadAllLines "RELEASE_NOTES.md" |> Fake.ReleaseNotesHelper.parseReleaseNotes
let configuration = environVarOrDefault "CONFIGURATION" "Release"

Target "Clean" (fun _ ->
  !!"./src/**/bin/" ++ "./src/**/obj/" ++ "./artifacts"
  |> CleanDirs)

Target "ProjectVersion" (fun _ ->
  !! "src/*/*.csproj"
  |> Seq.iter (fun file ->
    printfn "Changing file %s" file
    XMLHelper.XmlPoke file "Project/PropertyGroup/Version/text()" release.NugetVersion)
)

Target "Restore" (fun _ ->
  DotNetCli.Restore (fun p -> { p with WorkingDir = "src" })
)

/// This also restores.
Target "Build" (fun _ ->
  DotNetCli.Build (fun p ->
  { p with
      Configuration = configuration
      Project = "./Aliyun.Api.Log/Aliyun.Api.Log.sln"
  })
)

Target "Tests" (fun _ ->
  let commandLine (file: string) =
    let projectName = file.Substring(0, file.Length - ".fsproj".Length) |> Path.GetFileName
    let path = Path.GetDirectoryName file
    sprintf "%s/bin/%s/netcoreapp2.0/%s.dll --summary" path configuration projectName
  Seq.concat [
    !! "test/**/*.fsproj"
  ]
  |> Seq.iter (commandLine >> DotNetCli.RunCommand id))

let packParameters name =
  [ "--no-build"
    "--no-restore"
  ]
  |> String.concat " "

Target "Pack" (fun _ ->
  !! "src/**/*.csproj"
  |> Seq.iter (fun proj ->
    let path = proj.Substring(0, proj.Length - ".csproj".Length)
    let name = System.IO.Path.GetFileName path
    DotNetCli.RunCommand id (
      sprintf
        "pack %s -c %s -o ./bin %s"
        proj configuration (packParameters name))
  )
)

let envRequired k =
  let v = Environment.GetEnvironmentVariable k
  if isNull v then failwithf "Missing environment key '%s'." k
  v

Target "Push" (fun _ ->
  Paket.Push (fun p -> { p with WorkingDir = "src"; ApiKey = (envRequired "NUGET_API_KEY") }))


#load "paket-files/build/fsharp/FAKE/modules/Octokit/Octokit.fsx"
Target "Release" (fun _ ->
  let gitOwner, gitName = "lust4life", "aliyun-log-csharp-sdk"
  let gitOwnerName = gitOwner + "/" + gitName
  let remote =
      Git.CommandHelper.getGitResult "" "remote -v"
      |> Seq.tryFind (fun s -> s.EndsWith "(push)" && s.Contains gitOwnerName)
      |> function None -> "git@github.com:lust4life/aliyun-log-csharp-sdk.git"
                | Some s -> s.Split().[0]

  Git.Staging.StageAll ""
  Git.Commit.Commit "" (sprintf "Bump version to %s" release.NugetVersion)
  Git.Branches.pushBranch "" remote (Git.Information.getBranchName "")

  Git.Branches.tag "" release.NugetVersion
  Git.Branches.pushTag "" remote release.NugetVersion

  Octokit.createClientWithToken (envRequired "GITHUB_TOKEN")
  |> Octokit.createDraft gitOwner gitName release.NugetVersion
      (Option.isSome release.SemVer.PreRelease) release.Notes
  |> Octokit.releaseDraft
  |> Async.RunSynchronously
)


"Clean"
  ==> "ProjectVersion"
  ==> "Build"
  ==> "Tests"
  ==> "Pack"
  ==> "Push"
  ==> "Release"

RunTargetOrDefault "Pack"
