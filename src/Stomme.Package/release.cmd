dotnet build -c Release ..\Stomme.slnx
FixProps.exe
nuget.exe pack Stomme.Client.nuspec
nuget.exe pack Stomme.Server.nuspec
nuget.exe pack Stomme.Shared.nuspec                  
nuget.exe pack Stomme.Framework.nuspec
dotnet publish ..\Stomme.Server\Stomme.Server.csproj /p:Configuration=Release
rmdir /Q /S "..\Stomme.Server\bin\Release\net10.0\publish\Content"
rmdir /Q /S "..\Stomme.Server\bin\Release\net10.0\publish\wwwroot\Content"
rmdir /Q /S "..\Stomme.Server\bin\Release\net10.0\publish\runtimes\android-arm"
rmdir /Q /S "..\Stomme.Server\bin\Release\net10.0\publish\runtimes\android-arm64"
rmdir /Q /S "..\Stomme.Server\bin\Release\net10.0\publish\runtimes\android-x64"
rmdir /Q /S "..\Stomme.Server\bin\Release\net10.0\publish\runtimes\android-x86"
rmdir /Q /S "..\Stomme.Server\bin\Release\net10.0\publish\runtimes\ios-arm"
rmdir /Q /S "..\Stomme.Server\bin\Release\net10.0\publish\runtimes\ios-arm64"
rmdir /Q /S "..\Stomme.Server\bin\Release\net10.0\publish\runtimes\iossimulator-arm64"
rmdir /Q /S "..\Stomme.Server\bin\Release\net10.0\publish\runtimes\iossimulator-x64"
rmdir /Q /S "..\Stomme.Server\bin\Release\net10.0\publish\runtimes\iossimulator-x86"
rmdir /Q /S "..\Stomme.Server\bin\Release\net10.0\publish\wwwroot\Modules\Templates"
rmdir /Q /S "..\Stomme.Server\bin\Release\net10.0\publish\wwwroot\Themes\Templates"
C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe ".\install.ps1"
del "..\Stomme.Server\bin\Release\net10.0\publish\appsettings.json"
del "..\Stomme.Server\bin\Release\net10.0\publish\web.config"
C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe ".\upgrade.ps1"
dotnet build -c Release ..\Stomme.Updater.slnx
dotnet publish ..\Stomme.Updater\Stomme.Updater.csproj /p:Configuration=Release
nuget.exe pack Stomme.Updater.nuspec
pwsh -NoProfile -File ..\..\scripts\pack-template.ps1 -Output .
pause 

