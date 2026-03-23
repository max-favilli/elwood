/**
 * Extension method registry. External packages register custom methods here;
 * the evaluator consults the registry when a method name is not a built-in.
 */

export type ElwoodMethodHandler = (target: unknown, args: unknown[]) => unknown;

const registry = new Map<string, ElwoodMethodHandler>();

/** Register a custom method provided by an extension package. */
export function registerMethod(name: string, handler: ElwoodMethodHandler): void {
  registry.set(name, handler);
}

/** @internal — used by the evaluator to look up extension methods. */
export function getExtensionMethod(name: string): ElwoodMethodHandler | undefined {
  return registry.get(name);
}
