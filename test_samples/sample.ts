interface Preview {
  path: string;
  sizeKb: number;
}

export function label(p: Preview): string {
  return `${p.path} (${p.sizeKb} KB)`;
}
