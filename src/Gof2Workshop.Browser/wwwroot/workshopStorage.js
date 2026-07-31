const prefix = 'gof2workshop.';

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
