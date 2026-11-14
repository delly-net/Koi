#!/bin/bash
echo "[==========拉取代码==========]"
cd /project/github/delly-net/Koi
git checkout main
git pull
cd /project/github/delly-net/Koi/ui
echo "[==========更新依赖==========]"
npm install
echo "[==========编译代码==========]"
npm run build
echo "[==========复制文件==========]"
mkdir -p /publish/github/delly-net/Koi/files/wwwroot
rm -rf /publish/github/delly-net/Koi/files/wwwroot/*
cp -r ./dist/* /publish/github/delly-net/Koi/files/wwwroot/
rm -rf /publish/github/delly-net/Koi/files/wwwroot/conf/setting.json
cp -r /publish/github/delly-net/Koi/conf/setting.json /publish/github/delly-net/Koi/files/wwwroot/conf/setting.json
echo "[==========编译完成==========]"