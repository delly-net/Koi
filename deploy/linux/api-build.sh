#!/bin/bash
echo "[==========拉取代码==========]"
cd /project/delly-net/Koi
git checkout main
git pull
echo "[==========编译代码==========]"
mkdir -p /project/delly-net/publish/Koi/files
cd /project/delly-net/Koi/api
/usr/bin/dotnet build Koi.Api.csproj -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o /project/delly-net/publish/Koi/files
echo "[==========编译完成==========]"