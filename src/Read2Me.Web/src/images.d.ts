declare module '*.png' {
  /** The bundled file's URL (Bun's file loader). */
  const url: string;
  export default url;
}
