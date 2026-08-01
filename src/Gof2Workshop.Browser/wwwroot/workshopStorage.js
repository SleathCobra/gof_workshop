const prefix = 'gof2workshop.';
const databaseName = 'gof2workshop';
const databaseVersion = 1;
const workspaceStore = 'workspaces';

function openDatabase() {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open(databaseName, databaseVersion);
    request.onupgradeneeded = () => {
      const database = request.result;
      if (!database.objectStoreNames.contains(workspaceStore)) {
        database.createObjectStore(workspaceStore);
      }
    };
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error ?? new Error('IndexedDB open failed.'));
  });
}

async function useStore(mode, action) {
  const database = await openDatabase();
  try {
    return await new Promise((resolve, reject) => {
      const transaction = database.transaction(workspaceStore, mode);
      const store = transaction.objectStore(workspaceStore);
      let result;
      transaction.oncomplete = () => resolve(result);
      transaction.onabort = () => reject(transaction.error ?? new Error('IndexedDB transaction aborted.'));
      transaction.onerror = () => reject(transaction.error ?? new Error('IndexedDB transaction failed.'));
      result = action(store);
      if (result instanceof IDBRequest) {
        result.onsuccess = () => { result = result.result; };
      }
    });
  } finally {
    database.close();
  }
}

export function getSetting(key) {
  return globalThis.localStorage.getItem(prefix + key);
}

export function setSetting(key, value) {
  globalThis.localStorage.setItem(prefix + key, value);
}

export function clearWorkshopData() {
  const keys = [];
  for (let index = 0; index < globalThis.localStorage.length; index++) {
    const key = globalThis.localStorage.key(index);
    if (key && key.startsWith(prefix)) keys.push(key);
  }
  for (const key of keys) globalThis.localStorage.removeItem(key);
}

export function getWorkshopStorageBytes() {
  let characters = 0;
  for (let index = 0; index < globalThis.localStorage.length; index++) {
    const key = globalThis.localStorage.key(index);
    if (key && key.startsWith(prefix)) {
      characters += key.length + (globalThis.localStorage.getItem(key)?.length ?? 0);
    }
  }
  return characters * 2;
}

export async function saveWorkspace(key, json) {
  await useStore('readwrite', store => store.put(json, key));
  return json.length.toString();
}

export async function loadWorkspace(key) {
  const result = await useStore('readonly', store => store.get(key));
  return typeof result === 'string' ? result : null;
}

export async function removeWorkspace(key) {
  await useStore('readwrite', store => store.delete(key));
  return 'removed';
}

export async function getStorageEstimate() {
  if (!navigator.storage?.estimate) return JSON.stringify({ usage: 0, quota: 0 });
  const estimate = await navigator.storage.estimate();
  return JSON.stringify({ usage: estimate.usage ?? 0, quota: estimate.quota ?? 0 });
}

export async function clearAllWorkshopData() {
  clearWorkshopData();
  await new Promise((resolve, reject) => {
    const request = indexedDB.deleteDatabase(databaseName);
    request.onsuccess = () => resolve();
    request.onerror = () => reject(request.error ?? new Error('IndexedDB deletion failed.'));
    request.onblocked = () => reject(new Error('IndexedDB deletion is blocked by another Workshop tab.'));
  });
  return 'cleared';
}
