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

echo Installing the locked dashboard dependencies...
call npm ci
if errorlevel 1 (
  echo ERROR: npm ci failed with exit code %ERRORLEVEL%.
  goto :failure
)

echo Installing the explicitly managed Chromium browser...
call node_modules\.bin\playwright.cmd install chromium
if errorlevel 1 (
  echo ERROR: Chromium installation failed with exit code %ERRORLEVEL%.
  goto :failure
)

echo Verifying the dashboard...
call npm run check
if errorlevel 1 (
  echo ERROR: Dashboard verification failed with exit code %ERRORLEVEL%.
  goto :failure
)

echo Setup complete. Run start-dashboard.cmd to open the dashboard.
exit /b 0

:failure
set "EXIT_CODE=%ERRORLEVEL%"
if "%EXIT_CODE%"=="0" set "EXIT_CODE=1"
echo Setup stopped. Correct the error above and run setup-dashboard.cmd again.
if not defined CHD_NO_PAUSE pause
exit /b %EXIT_CODE%
