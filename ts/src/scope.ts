/**
 * Variable scope for Elwood evaluation. Supports nested scopes (let bindings, lambdas).
 */
export class Scope {
  private vars = new Map<string, unknown>();

  constructor(private parent?: Scope) {}

  set(name: string, value: unknown): void {
    this.vars.set(name, value);
  }

  get(name: string): unknown | undefined {
    if (this.vars.has(name)) return this.vars.get(name);
    return this.parent?.get(name);
  }

  has(name: string): boolean {
    if (this.vars.has(name)) return true;
    return this.parent?.has(name) ?? false;
  }

  child(): Scope {
    return new Scope(this);
  }
}
