#/bin/bash
sh api-build.sh
sh ui-build.sh

month=$(date +%Y%m)
day=$(date +%Y%m%d)
./package.sh all /publish/github/delly-net/Koi/packages/${month}/${day}