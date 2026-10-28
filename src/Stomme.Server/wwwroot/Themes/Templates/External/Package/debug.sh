#!/bin/bash

TargetFramework=$1
ProjectName=$2

cp -f "../Client/bin/Debug/$TargetFramework/$ProjectName$.Client.Stomme.dll" "../../[RootFolder]/Stomme.Server/bin/Debug/$TargetFramework/"
cp -f "../Client/bin/Debug/$TargetFramework/$ProjectName$.Client.Stomme.pdb" "../../[RootFolder]/Stomme.Server/bin/Debug/$TargetFramework/"
cp -rf "../Client/wwwroot/"* "../../[RootFolder]/Stomme.Server/wwwroot/_content/%ProjectName%/"