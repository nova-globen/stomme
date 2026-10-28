TargetFramework=$1
ProjectName=$2

find . -name "*.nupkg" -delete
"..\..\Stomme\Stomme.Package\FixProps.exe"
"..\..\[RootFolder]\Stomme.Package\nuget.exe" pack %ProjectName%.nuspec -Properties targetframework=%TargetFramework%;projectname=%ProjectName%
cp -f "*.nupkg" "..\..\[RootFolder]\Stomme.Server\Packages\"