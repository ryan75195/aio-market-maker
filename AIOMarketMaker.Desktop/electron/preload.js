const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('electronAPI', {
  getConfig: () => ipcRenderer.invoke('get-config'),
  getConfigPath: () => ipcRenderer.invoke('get-config-path'),
  saveConfig: (config) => ipcRenderer.invoke('save-config', config)
});
