#!/bin/bash
echo "[==========编译代码==========]"
mkdir -p /publish/github/delly-net/Koi/files
cd /project/github/delly-net/Koi/api
/usr/bin/dotnet build Koi.Api.csproj -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o /publish/github/delly-net/Koi/files
echo "[==========编译完成==========]"