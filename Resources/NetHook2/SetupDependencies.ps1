if (!$env:DevEnvDir) {
    $vsWhere = Join-Path -Path ${env:ProgramFiles(x86)} -ChildPath 'Microsoft Visual Studio\Installer\vswhere.exe'
    $vsInstallDir = (. $vsWhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath) |
        Select-Object -First 1

    $modulePath = Join-Path -Path $vsInstallDir -ChildPath 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll'
    if (Test-Path -Path $modulePath -PathType Leaf) {
        Import-Module $modulePath
    } else {
        throw "Failed to location Visual Studio PowerShell module"
    }

    Enter-VsDevShell -VsInstallPath $vsInstallDir
}

$vcpkg = Get-Command -Name vcpkg -CommandType Application -ErrorAction SilentlyContinue
if (!$vcpkg) {
    if ($env:CI) {
        Write-Host "CI environment detected"

        Write-Host "Downloading vcpkg"
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
    } else {
        Write-Warning "vcpkg is required but not found. Please see https://vcpkg.io/en/getting-started to install it"
    }
} else {
    Write-Host "Found vcpkg at $($vcpkg.Source)"
}

if ($env:CI) {
    vcpkg integrate install
}

# todo: compile or download protoc and generate steammessages_base.pb.{h|cpp}
