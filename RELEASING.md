# Publicar una versión

1. Actualiza la versión en `src/Program.cs`, `installer/Installer.cs` e `installer/installer.manifest`.
2. Ejecuta `powershell -ExecutionPolicy Bypass -File .\build-installer.ps1 -Clean`.
3. Prueba una instalación limpia, una actualización y una desinstalación completa.
4. Calcula el hash del instalador:

   ```powershell
   Get-FileHash -Algorithm SHA256 .\dist\PcTemp-Setup-*.exe
   ```

5. Crea una publicación en GitHub Releases y adjunta el instalador de `dist` y, si se desea, un ZIP de `PcTemp-Release`.

Los archivos generados no deben confirmarse en el repositorio; GitHub Actions también produce ambos artefactos en cada compilación.
