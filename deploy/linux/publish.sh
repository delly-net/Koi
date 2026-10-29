#/bin/bash
mkdir -p /opt/delly-net/Koi
rm -rf /opt/delly-net/Koi/*
cp -r /project/delly-net/publish/Koi/files/* /opt/delly-net/Koi/
chmod +x /opt/delly-net/Koi/koi