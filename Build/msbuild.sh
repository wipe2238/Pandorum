#!/bin/bash

export DOTNET_CLI_TELEMETRY_OPTOUT=1
if   [ -f "/c/Program Files/Microsoft Visual Studio/Installer/vswhere.exe" ]; then
  vswhere="/c/Program Files/Microsoft Visual Studio/Installer/vswhere.exe"
elif [ -f "/c/Program Files (x86)/Microsoft Visual Studio/Installer/vswhere.exe" ]; then
  vswhere="/c/Program Files (x86)/Microsoft Visual Studio/Installer/vswhere.exe"
else
   echo "vswhere.exe not found"
   exit 1
fi

"$("$vswhere" -latest -requires Microsoft.Component.MSBuild -find MSBuild/**/Bin/MSBuild.exe)" $*
