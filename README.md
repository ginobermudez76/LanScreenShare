# LanScreenShare

Aplicación de escritorio para transmisión de pantalla en tiempo real, audio del sistema, control remoto, transferencia de archivos y extensión inalámbrica de monitor en redes locales (LAN), desarrollada en **C# (.NET 9.0)** y **Avalonia UI 12**.

---

## Características principales

- **Captura de vídeo de baja latencia:** DirectX 11 DXGI Output Duplication con selección multi-GPU por adaptador y salida (`DxgiOutputSelector`), renderizado del cursor real del sistema operativo (`CursorSpriteRenderer`), además de captura de ventanas y escritorios vía Win32 GDI.
- **Codificación H.264 acelerada y adaptativa:** Media Foundation con detección dinámica de codificadores hardware (`MFTEnumEx`: NVIDIA NVENC, Intel QuickSync, AMD AMF), fallback explícito a software / JPEG con SkiaSharp y control de congestión adaptativo (**ABR** basado en AIMD).
- **Audio del sistema y micrófono:** Captura con WASAPI Loopback, aislamiento de audio por proceso específico (`ProcessLoopbackAudioCapture`), micrófono bidireccional y controlador de sincronización audiovisual (`AudioVideoSyncController`).
- **Control remoto y Modo Videojuegos 3D:** Inyección de teclado y ratón (modo absoluto y delta relativo para juegos 3D), sincronización de portapapeles y protección contra entradas retenidas (`InjectedInputTracker`).
- **Modo Pantalla Inalámbrica (Monitor Virtual Extendido):** Extiende el escritorio del anfitrión hacia otro equipo en la LAN utilizando `Virtual-Display-Driver` (o backend `ParsecVDD`), señalización inversa autenticada por mTLS, preservación/restauración de topología Windows CCD y proceso independiente `LanScreenShare.DisplayWatchdog` para limpieza ante cierres inesperados.
- **Soporte para UAC y Escritorio Seguro (Winlogon):** Arquitectura híbrida mediante el servicio de Windows `LanScreenShare.SecureHost` (`NT AUTHORITY\SYSTEM`) y comunicación IPC por Named Pipes con ACL restringida para operar pantallas de elevación UAC e inicio de sesión.
- **Seguridad criptográfica (mTLS):** Autenticación mutua TLS 1.3 / 1.2 con certificados ECDSA P-256, emparejamiento por código SAS de 6 dígitos, tokens efímeros de un solo uso por canal secundario (`ChannelAttachmentToken`), almacenamiento cifrado con DPAPI (Windows) o AES-256-GCM (multiplataforma), ventana de gestión y revocación de dispositivos confiables (`TrustedDevicesWindow`) y sincronización granular de permisos del visor.
- **Transferencia rápida de archivos:** Canal dedicado sobre mTLS (puerto TCP `5004`) con streaming en bloques de 64 KB, verificación de integridad SHA-256, soporte Drag & Drop y carpeta de descargas configurable desde la interfaz.
- **Interfaz flexible:** Perfiles rápidos de transmisión, modo avanzado, cambio remoto de fuente de captura, ventana secundaria independiente (*popout* / pantalla completa) y asistente integrado para la instalación del driver virtual (`VddDriverInstallerWindow`).

---

## Arquitectura de red y puertos

| Puerto | Protocolo | Servicio | Función |
| :--- | :--- | :--- | :--- |
| `5000` | UDP | `DiscoveryService` | Descubrimiento LAN por broadcast dirigido de subred y anuncio de disponibilidad para Pantalla Inalámbrica |
| `5001` | TCP / mTLS | `TcpStreamServer` / `TcpStreamClient` | Canal principal de vídeo (H.264 / JPEG), handshake de sesión y telemetría |
| `5002` | TCP / mTLS | `TcpAudioServer` / `TcpAudioClient` | Transmisión de audio PCM (WASAPI / micrófono) |
| `5003` | TCP / mTLS | `TcpControlServer` / `TcpControlClient` | Control remoto bidireccional, cambio de fuente, permisos y portapapeles |
| `5004` | TCP / mTLS | `TcpFileTransferServer` / `TcpFileTransferClient` | Transferencia de archivos con protocolo binario `LSFT` y hash SHA-256 |
| `5005` | TCP / mTLS | `DisplaySignalingServer` / `DisplaySignalingClient` | Negociación de capacidades e invitación inversa para Modo Pantalla Inalámbrica |

---

## Estructura de la solución

La solución `LanScreenShare.sln` está dividida en 7 proyectos:

- **`LanScreenShare.Core`**: Contratos (`IScreenCaptureService`, `IAudioCaptureService`, `IInputSimulatorService`), modelos de dominio, autoridad de sesión, almacén de confianza, `AdaptiveBitrateController`, `AudioVideoSyncController` y `VirtualDisplayCoordinator`.
- **`LanScreenShare.Network`**: Descubrimiento UDP, transporte TCP con `SslStream` / mTLS, protocolos binarios de vídeo/audio/control/archivos y coordinación de proyección inalámbrica (`VirtualDisplayProjectionHost` y `VirtualDisplayProjectionReceiver`).
- **`LanScreenShare.Media`**: Implementaciones nativas de Windows (DXGI, GDI, Media Foundation H.264, WASAPI, `SendInput`), backends de monitor virtual, gestión de topología CCD y cliente IPC hacia `SecureHost`.
- **`LanScreenShare.Desktop`**: Interfaz gráfica multiplataforma construida con Avalonia UI 12.0.2 (.NET 9.0) y ventana emergente de visualización.
- **`LanScreenShare.DisplayWatchdog`**: Ejecutable auxiliar que supervisa la vida del proceso principal para desmontar monitores virtuales y restaurar la topología de pantallas tras una terminación abrupta.
- **`LanScreenShare.SecureHost`**: Servicio de Windows con privilegios `SYSTEM` para captura e inyección en escritorios protegidos (UAC / Winlogon).
- **`LanScreenShare.Tests`**: Suite de pruebas unitarias y de integración (MSTest).

Además, el repositorio incluye:
- **`installer/`**: Scripts PowerShell y definición de Inno Setup (`LanScreenShare.iss`) para generar `LanScreenShare-Setup.exe` con empaquetado de `Drivers\VDD`, reglas de firewall, `uiAccess` y registro del servicio `SecureHost`.
- **`docs/`**: Registros de decisiones de arquitectura (`ADR-001`, `ADR-002`) y plan de refinamiento.
- **`scripts/verify-all.ps1`**: Verificación local de formato, compilación y pruebas.

---

## Requisitos y ejecución rápida

### Requisitos previos
- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- **Windows 10 / 11 (x64)** para captura DXGI, aceleración H.264 vía Media Foundation, audio WASAPI y Modo Pantalla Inalámbrica (la capa Core/Network/UI compila de forma multiplataforma).
- **Inno Setup 6** (solo si deseas compilar el instalador `.exe`): `winget install JRSoftware.InnoSetup`

### Clonar, compilar y ejecutar

```powershell
git clone https://github.com/ginobermudez76/LanScreenShare.git
cd LanScreenShare

dotnet build LanScreenShare.sln --configuration Release
dotnet run --project .\LanScreenShare.Desktop --configuration Release
```

### Verificar formato y ejecutar pruebas

```powershell
dotnet format --verify-no-changes --verbosity normal
dotnet test LanScreenShare.sln --configuration Release --no-build
```

### Generar el instalador de Windows

```powershell
.\installer\Build-Installer.ps1 -Version "1.0.0"
```

La salida se genera en `installer\output\LanScreenShare-Setup.exe`. Si necesitas compilar sin descargar el paquete del driver virtual desde GitHub, usa `-SkipVddDownload`.

---

## Documentación técnica

- [**`PROJECT_ROADMAP.md`**](PROJECT_ROADMAP.md): Arquitectura detallada, tecnologías, estado actualizado de hitos y diagrama de clases.
- [**`docs/adr/ADR-001-SESSION-SECURITY-AND-PAIRING.md`**](docs/adr/ADR-001-SESSION-SECURITY-AND-PAIRING.md): Arquitectura de seguridad de sesión, autenticación mutua (mTLS) y emparejamiento.
- [**`docs/adr/ADR-002-UAC-AND-SECURE-DESKTOP-HANDLING.md`**](docs/adr/ADR-002-UAC-AND-SECURE-DESKTOP-HANDLING.md): Manejo de UAC, Winlogon y escritorio seguro.
- [**`docs/PLAN_REFINAMIENTO_ARQUITECTURA_Y_UI.md`**](docs/PLAN_REFINAMIENTO_ARQUITECTURA_Y_UI.md): Plan histórico de refinamiento arquitectónico y mejoras de UI.
