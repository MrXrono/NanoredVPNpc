@rem
@rem NanoredVPN - Gradle build script for Windows
@rem

@if "%DEBUG%" == "" @echo off

@rem Set local scope for the variables with windows NT shell
if "%OS%"=="Windows_NT" setlocal

set DIRNAME=%~dp0
if "%DIRNAME%" == "" set DIRNAME=.
set APP_BASE_NAME=%~n0
set APP_HOME=%DIRNAME%

@rem Resolve any "." and ".." in APP_HOME to make it shorter.
for %%i in ("%APP_HOME%") do set APP_HOME=%%~fi

@rem === JDK 21 (required by AGP 9.0) ===
if not defined JAVA_HOME (
    if exist "C:\Program Files\Java\jdk-21.0.8" (
        set "JAVA_HOME=C:\Program Files\Java\jdk-21.0.8"
    ) else (
        for /d %%j in ("C:\Program Files\Java\jdk-21*") do set "JAVA_HOME=%%j"
    )
)
if not defined JAVA_HOME (
    echo ERROR: JDK 21 not found. Install it or set JAVA_HOME.
    goto fail
)

@rem === Android SDK ===
if not defined ANDROID_HOME (
    if exist "C:\Android\sdk" (
        set "ANDROID_HOME=C:\Android\sdk"
    ) else if exist "%LOCALAPPDATA%\Android\Sdk" (
        set "ANDROID_HOME=%LOCALAPPDATA%\Android\Sdk"
    )
)
if not defined ANDROID_HOME (
    echo ERROR: Android SDK not found. Install it or set ANDROID_HOME.
    goto fail
)
set "ANDROID_SDK_ROOT=%ANDROID_HOME%"

@rem === Gradle cache directory ===
if not defined GRADLE_USER_HOME (
    set "GRADLE_USER_HOME=%USERPROFILE%\.gradle"
)

@rem === local.properties ===
if not exist "%APP_HOME%\local.properties" (
    echo sdk.dir=%ANDROID_HOME:\=/%>"%APP_HOME%\local.properties"
    echo [auto] Created local.properties
)

@rem === Keystore ===
set "KEYSTORE_PATH=%APP_HOME%\nanored.keystore"
if not exist "%KEYSTORE_PATH%" (
    echo [auto] Keystore not found, generating: %KEYSTORE_PATH%
    "%JAVA_HOME%\bin\keytool" -genkeypair -v -keystore "%KEYSTORE_PATH%" -alias nanored -keyalg RSA -keysize 2048 -validity 10000 -storepass nanored123 -keypass nanored123 -dname "CN=NanoredVPN, OU=VPN, O=Nanored, L=City, ST=State, C=US"
    if not exist "%KEYSTORE_PATH%" (
        echo ERROR: Failed to generate keystore.
        goto fail
    )
    echo [auto] Keystore created successfully.
) else (
    echo [ok] Keystore found: %KEYSTORE_PATH%
)

@rem JVM options
set DEFAULT_JVM_OPTS="-Xmx4096m" "-Xms512m" "-Dfile.encoding=UTF-8"

@rem Find java.exe
set JAVA_HOME=%JAVA_HOME:"=%
set JAVA_EXE=%JAVA_HOME%\bin\java.exe

if exist "%JAVA_EXE%" goto execute

echo.
echo ERROR: JAVA_HOME is set to an invalid directory: %JAVA_HOME%
echo.
goto fail

:execute
@rem Print build environment
echo =========================================
echo  NanoredVPN Build
echo  JAVA_HOME    = %JAVA_HOME%
echo  ANDROID_HOME = %ANDROID_HOME%
echo  GRADLE_HOME  = %GRADLE_USER_HOME%
echo  KEYSTORE     = %KEYSTORE_PATH%
echo  PROJECT      = %APP_HOME%
echo =========================================

@rem Setup the command line
set CLASSPATH=%APP_HOME%\gradle\wrapper\gradle-wrapper.jar

@rem Execute Gradle
"%JAVA_EXE%" %DEFAULT_JVM_OPTS% %JAVA_OPTS% %GRADLE_OPTS% "-Dorg.gradle.appname=%APP_BASE_NAME%" -classpath "%CLASSPATH%" org.gradle.wrapper.GradleWrapperMain %*

:end
@rem End local scope for the variables with windows NT shell
if "%ERRORLEVEL%"=="0" goto mainEnd

:fail
rem Set variable GRADLE_EXIT_CONSOLE if you need the _script_ return code instead of
rem the _cmd.exe /c_ return code!
if  not "" == "%GRADLE_EXIT_CONSOLE%" exit 1
exit /b 1

:mainEnd
if "%OS%"=="Windows_NT" endlocal

:omega
