#!/bin/bash
echo "[==========拉取代码==========]"
cd /project/delly-net/Koi
git checkout main
git pull
cd /project/delly-net/Koi/ui
echo "[==========更新依赖==========]"
npm install
echo "[==========编译代码==========]"
npm run build
echo "[==========复制文件==========]"
mkdir -p /project/delly-net/publish/Koi/files/wwwroot
rm -rf /project/delly-net/publish/Koi/files/wwwroot/*
cp -r ./dist/* /project/delly-net/publish/Koi/files/wwwroot/
rm -rf /project/delly-net/publish/Koi/files/wwwroot/conf/setting.json
cp -r /project/delly-net/publish/Koi/conf/setting.json /project/delly-net/publish/Koi/files/wwwroot/conf/setting.json
echo "[==========编译完成==========]"