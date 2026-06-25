Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

dotnet build .\CodexUpdater.sln
dotnet run --project .\tests\CodexUpdater.Tests\CodexUpdater.Tests.csproj
dotnet publish .\src\CodexUpdater.App\CodexUpdater.App.csproj -c Release -r win-x64 --self-contained false -o .\publish
