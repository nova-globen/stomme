@echo off
set TargetFramework=%1
set ProjectName=%2

del "*.nupkg"
"..\..\[RootFolder]\Stomme.Package\FixProps.exe"
"..\..\[RootFolder]\Stomme.Package\nuget.exe" pack %ProjectName%.nuspec -Properties targetframework=%TargetFramework%;projectname=%ProjectName%
XCOPY "*.nupkg" "..\..\[RootFolder]\Stomme.Server\Packages\" /Y