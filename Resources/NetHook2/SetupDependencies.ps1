if (!$env:DevEnvDir) {
    $vsWhere = Join-Path -Path ${env:ProgramFiles(x86)} -ChildPath 'Microsoft Visual Studio\Installer\vswhere.exe'
    $installDir = (. $vsWhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath) |
        Select-Object -First 1

    $modulePath = Join-Path -Path $installDir -ChildPath 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll'
    if (Test-Path -Path $modulePath -PathType Leaf) {
        Import-Module $modulePath
    } else {
        throw "Failed to location Visual Studio PowerShell module"
    }

    Enter-VsDevShell
}

$vcpkg = Get-Command -Name vcpkg -CommandType Application -ErrorAction SilentlyContinue
if (!$vcpkg) {
    if ($env:CI)
    {
        $vcpkgDir = Join-Path -Path (Get-Location.Path) -ChildPath 'vcpkg'
        if (!(Test-Path -Path $vcpkgDir)) {
            git clone --depth 1 "https://github.com/Microsoft/vcpkg.git"
        } else {
            Set-Location vcpkg
            git pull
            Set-Location ..
        }

        .\vcpkg\bootstrap-vcpkg.bat

        $env:Path = "PATH=$vcpkgDir;$env:Path"
        vcpkg integrate install
    } else {
        Write-Warning "vcpkg is required but not found. Please see https://vcpkg.io/en/getting-started to install it"
    }
}

# todo: compile or download protoc and generate steammessages_base.pb.{h|cpp}
