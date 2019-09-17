@echo off

set /p v="What version is this? "

set /p type="dev, uat or prod? "

docker build -f ./Dockerfile -t public-cloud-portal:edpc_cloudostunnel_%type%_v%v% .
docker tag public-cloud-portal:edpc_cloudostunnel_%type%_v%v% eddevops1/public-cloud-portal:edpc_cloudostunnel_%type%_v%v%
docker push eddevops1/public-cloud-portal:edpc_cloudostunnel_%type%_v%v%
Pause