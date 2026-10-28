@echo off
set TargetFramework=%1
set ProjectName=%2

XCOPY "..\Client\bin\Debug\%TargetFramework%\%ProjectName%.Client.Stomme.dll" "..\..\[RootFolder]\Stomme.Server\bin\Debug\%TargetFramework%\" /Y
XCOPY "..\Client\bin\Debug\%TargetFramework%\%ProjectName%.Client.Stomme.pdb" "..\..\[RootFolder]\Stomme.Server\bin\Debug\%TargetFramework%\" /Y
XCOPY "..\Client\wwwroot\*" "..\..\Stomme\Stomme.Server\wwwroot\_content\%ProjectName%\" /Y /S /I