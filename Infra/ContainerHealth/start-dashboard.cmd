@echo off
setlocal
cd /d "%~dp0"

where node >nul 2>nul
if errorlevel 1 (
  echo ERROR: Node.js was not found. Install Node 24 LTS ^(24.18.0 or later, below 25^) from https://nodejs.org/ and retry.
  goto :failure
)

for /f "usebackq delims=" %%V in (`call node -p "process.versions.node"`) do set "NODE_VERSION=%%V"
call node -e "const [a,b,c]=process.argv[1].split('.').map(Number);process.exit(a===24&&(b>18||(b===18&&c>=0))?0:1)" "%NODE_VERSION%"
if errorlevel 1 (
  echo ERROR: Unsupported Node %NODE_VERSION%. Install Node 24 LTS ^(24.18.0 or later, below 25^).
  goto :failure
)

where npm >nul 2>nul
if errorlevel 1 (
  echo ERROR: npm was not found. Install the npm 11 toolchain supplied with Node 24 LTS.
  goto :failure
)

for /f "usebackq delims=" %%V in (`npm --version`) do set "NPM_VERSION=%%V"
call node -e "const [a,b,c]=process.argv[1].split('.').map(Number);process.exit(a===11&&(b>16||(b===16&&c>=0))?0:1)" "%NPM_VERSION%"
if errorlevel 1 (
  echo ERROR: Unsupported npm %NPM_VERSION%. Install npm 11.16.0 or later, below 12.
  goto :failure
)

if not exist package-lock.json (
  echo ERROR: package-lock.json is missing. Run setup-dashboard.cmd.
  goto :failure
)
if not exist node_modules\.bin\vite.cmd (
  echo ERROR: Local Vite is missing. Run setup-dashboard.cmd; startup never downloads dependencies.
  goto :failure
)
if not exist node_modules\.bin\tsc.cmd (
  echo ERROR: Local TypeScript is missing. Run setup-dashboard.cmd; startup never downloads dependencies.
  goto :failure
)

call npm start
set "EXIT_CODE=%ERRORLEVEL%"
if "%EXIT_CODE%"=="0" exit /b 0
echo ERROR: The dashboard stopped with exit code %EXIT_CODE%.
echo Check the Vite diagnostic above. If 127.0.0.1:5173 is occupied, close that process and retry.
if not defined CHD_NO_PAUSE pause
exit /b %EXIT_CODE%

:failure
set "EXIT_CODE=%ERRORLEVEL%"
if "%EXIT_CODE%"=="0" set "EXIT_CODE=1"
echo Startup stopped. Run setup-dashboard.cmd after correcting the prerequisite above.
if not defined CHD_NO_PAUSE pause
exit /b %EXIT_CODE%
