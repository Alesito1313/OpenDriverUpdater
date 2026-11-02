# OpenDriverUpdater

Herramienta **de código abierto** para Windows 10/11 que mantiene tus drivers al día de forma sencilla:

1. **Al abrirla**, analiza los componentes de tu PC y los drivers que tienen instalados.
2. **Comprueba** si existe una versión más reciente publicada por el fabricante.
3. **Tú eliges** qué drivers actualizar marcando las casillas.
4. **Con un botón** los descarga, los instala y te pregunta si quieres reiniciar.

Antes de instalar crea (opcionalmente) un **punto de restauración**, para poder volver atrás si algún driver da problemas.

## ¿De dónde salen los drivers?

Del **catálogo oficial de Windows Update**, a través del Agente de Windows Update (la misma API que usa Windows). Es ahí donde Intel, AMD, NVIDIA, Realtek, Qualcomm, etc. publican sus drivers **firmados y certificados (WHQL)** para cada modelo de hardware.

Ventajas de esta fuente:

- Los drivers son oficiales y están firmados: nada de webs de terceros ni ejecutables dudosos.
- Microsoft empareja cada driver con el *Hardware ID* exacto de tu dispositivo, así que no se instala nada incompatible.
- No hace falta mantener una base de datos propia.

Limitación honesta: algunos fabricantes (sobre todo **NVIDIA y AMD en tarjetas gráficas**) publican primero en su web y suben a Windows Update más tarde, por lo que la versión de su web puede ser más nueva. Ver la *hoja de ruta*.

## Requisitos

- Windows 10 u 11 (x64 o ARM64)
- Permisos de administrador (el programa los pide al abrirse)
- Conexión a Internet y el servicio «Windows Update» sin deshabilitar

## Compilar

Necesitas el [SDK de .NET 8](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
# Ejecutar en modo desarrollo
dotnet run --project src/OpenDriverUpdater

# Generar un único .exe autónomo (no requiere tener .NET instalado)
dotnet publish src/OpenDriverUpdater -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

El ejecutable queda en `publish/OpenDriverUpdater.exe`.

Si subes el proyecto a GitHub, el flujo de `.github/workflows/build.yml` compila el `.exe` automáticamente en cada *push* y lo deja descargable en la pestaña **Actions**.

## Estructura

```
src/OpenDriverUpdater/
├── Program.cs                      Punto de entrada
├── MainForm.cs                     Interfaz (lista con casillas, botones, progreso)
├── app.manifest                    Pide permisos de administrador
├── Models/Models.cs                Driver instalado, actualización, resultado
└── Services/
    ├── DriverScanner.cs            Lee los drivers instalados (WMI: Win32_PnPSignedDriver)
    ├── WindowsUpdateService.cs     Busca, descarga e instala drivers (Windows Update Agent)
    └── SystemActions.cs            Punto de restauración y reinicio
```

## Hoja de ruta (ideas para contribuir)

- [ ] Fuentes adicionales por fabricante (APIs de NVIDIA / AMD / Intel) para detectar versiones más nuevas que las de Windows Update
- [ ] Copia de seguridad de los drivers actuales (`pnputil /export-driver`) antes de actualizar
- [ ] Ocultar un driver para que no se vuelva a ofrecer
- [ ] Botón de cancelar durante la descarga
- [ ] Traducciones (inglés, catalán, etc.)
- [ ] Modo línea de comandos para automatizar (`--scan`, `--update-all`)

## Licencia

MIT — ver [LICENSE](LICENSE).
