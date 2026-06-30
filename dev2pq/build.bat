@echo off
cd /d "%~dp0"
call "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
set VCPKG=C:\Repo\vcpkg\installed\x64-windows
rem /O2 optimizes for speed. Do NOT add /DNDEBUG: it strips the mesher DCEL consistency asserts, turning clean aborts
rem on corrupt arrangements (fig16_1, fig19_1) into infinite hangs. /O2 alone keeps asserts active.
cl /nologo /std:c++20 /O2 /EHsc /DUSE_GMP_ENABLED /bigobj /I C:\Repo\avaxman\Directional\include /I C:\Repo\avaxman\Directional\external\eigen /I C:\Repo\avaxman\libhedra\include /I %VCPKG%\include dev2pq.cpp /Fe:dev2pq.exe /link %VCPKG%\lib\gmp.lib %VCPKG%\lib\gmpxx.lib
copy /y %VCPKG%\bin\gmp-10.dll . >nul
copy /y %VCPKG%\bin\gmpxx-4.dll . >nul
