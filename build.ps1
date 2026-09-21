param(
    [string]$LubanDir = 'D:/Workspace/WcDesignerDoc/luban/Tools/Luban',
    [switch]$Deploy
)

# Backward-compatible convenience wrapper. Deployment itself is implemented by
# the Luban.Extension.Deployer C# project, not by this script.
$arguments = @(
    'build',
    (Join-Path $PSScriptRoot 'Luban.Extensions.sln'),
    '-c', 'Release', '-m:1',
    "-p:LubanDir=$LubanDir"
)

if ($Deploy) {
    $arguments += '-p:DeployLubanExtensions=true'
}

& dotnet @arguments
exit $LASTEXITCODE
