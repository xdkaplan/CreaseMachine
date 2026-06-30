#include <iostream>
#include <fstream>
#include <vector>
#include <cmath>
#include <algorithm>
#include <string>
#include <map>
#include <vector>
#include <set>
#include <tuple>
#include <cstdlib>
#include <Eigen/Core>
#include <directional/TriMesh.h>
#include <directional/PCFaceTangentBundle.h>
#include <directional/CartesianField.h>
#include <directional/readOFF.h>
#include <directional/PolyVectorData.h>
#include <directional/polyvector_field.h>
#include <directional/polyvector_to_raw.h>
#include <directional/polyvector_iteration_functions.h>
#include <directional/principal_matching.h>
#include <directional/setup_integration.h>
#include <directional/integrate.h>
#include <directional/setup_mesher.h>
#include <directional/mesher.h>
#include <directional/branched_isolines.h>

// Dev2PQ §5.3-5.4 consolidation (flat-region part): merge adjacent COPLANAR faces of the polygonal mesh into single
// polygons, so a flat area becomes a few big planar panels instead of a quad grid. Curved strips (non-coplanar
// neighbours) are left untouched. Each merged group's outer boundary is traced from the directed half-edges whose
// reverse lies outside the group. Returns merged (Dout, Fout) over the SAME vertices V. Falls back to the original
// faces for any group whose boundary doesn't trace to a single clean loop (holes / pinches).
static void consolidateCoplanar(const Eigen::MatrixXd& V, const Eigen::VectorXi& Din, const Eigen::MatrixXi& Fin,
                                Eigen::VectorXi& Dout, Eigen::MatrixXi& Fout){
  int nf = Din.size();
  std::vector<Eigen::RowVector3d> N(nf), C(nf);
  for(int f=0; f<nf; f++){                                 // Newell normal + centroid per face
    Eigen::RowVector3d n(0,0,0), c(0,0,0); int d=Din(f);
    for(int k=0;k<d;k++){ Eigen::RowVector3d p=V.row(Fin(f,k)), q=V.row(Fin(f,(k+1)%d));
      n(0)+=(p(1)-q(1))*(p(2)+q(2)); n(1)+=(p(2)-q(2))*(p(0)+q(0)); n(2)+=(p(0)-q(0))*(p(1)+q(1)); c+=p; }
    double nl=n.norm(); N[f]= nl>1e-12? Eigen::RowVector3d(n/nl) : Eigen::RowVector3d(0,0,1); C[f]=c/std::max(1,d);
  }
  std::map<std::pair<int,int>, std::vector<int>> e2f;       // undirected edge -> incident faces
  for(int f=0; f<nf; f++){ int d=Din(f); for(int k=0;k<d;k++){ int a=Fin(f,k), b=Fin(f,(k+1)%d); e2f[std::make_pair(std::min(a,b),std::max(a,b))].push_back(f); } }
  std::vector<int> uf(nf); for(int i=0;i<nf;i++) uf[i]=i;   // union-find: merge coplanar adjacent faces
  auto find=[&](int x){ while(uf[x]!=x){ uf[x]=uf[uf[x]]; x=uf[x]; } return x; };
  const double cosTol=std::cos(2.0*3.141592653589793/180.0);
  for(auto& kv: e2f){ if(kv.second.size()!=2) continue; int f=kv.second[0], g=kv.second[1];
    if(std::abs(N[f].dot(N[g]))<cosTol) continue;                                  // normals parallel?
    double scale=(V.row(Fin(f,0))-V.row(Fin(f,1))).norm();
    if(std::abs((C[g]-C[f]).dot(N[f])) > 0.02*std::max(scale,1e-9)) continue;      // same plane?
    uf[find(f)]=find(g);
  }
  std::map<int,std::vector<int>> groups;
  for(int f=0; f<nf; f++) groups[find(f)].push_back(f);
  std::vector<int> Ds; std::vector<std::vector<int>> Fs;
  auto keepOriginal=[&](const std::vector<int>& fl){ for(int f: fl){ std::vector<int> p; for(int k=0;k<Din(f);k++) p.push_back(Fin(f,k)); Ds.push_back((int)p.size()); Fs.push_back(p);} };
  for(auto& gr: groups){
    auto& fl=gr.second;
    if(fl.size()==1){ keepOriginal(fl); continue; }
    std::map<std::pair<int,int>,bool> dir;                  // directed half-edges present in the group
    for(int f: fl){ int d=Din(f); for(int k=0;k<d;k++) dir[std::make_pair(Fin(f,k),Fin(f,(k+1)%d))]=true; }
    std::map<int,int> nxt;                                  // boundary: vertex a -> next vertex b
    bool manifold=true;
    for(int f: fl){ int d=Din(f); for(int k=0;k<d;k++){ int a=Fin(f,k), b=Fin(f,(k+1)%d);
      if(!dir.count(std::make_pair(b,a))){ if(nxt.count(a)) manifold=false; nxt[a]=b; } } }
    if(!manifold || nxt.empty()){ keepOriginal(fl); continue; }
    int start=nxt.begin()->first, cur=start; std::vector<int> poly; int guard=0; bool ok=true;
    do{ poly.push_back(cur); if(!nxt.count(cur)){ ok=false; break; } cur=nxt[cur]; if(++guard>(int)nxt.size()+2){ ok=false; break; } } while(cur!=start);
    if(ok && (int)poly.size()==(int)nxt.size() && poly.size()>=3){ Ds.push_back((int)poly.size()); Fs.push_back(poly); }   // one clean loop covering all boundary verts
    else keepOriginal(fl);
  }
  int mf=(int)Ds.size(), maxd=1; for(int d: Ds) maxd=std::max(maxd,d);
  Dout.resize(mf); Fout.setZero(mf, maxd);
  for(int f=0; f<mf; f++){ Dout(f)=Ds[f]; for(int k=0;k<Ds[f];k++) Fout(f,k)=Fs[f][k]; }
}

// The lacing discriminator (the user's idea): a TRUE ruling is a straight generator that lies ON the developable, but a
// FLIPPED "ruling" is a long chord ACROSS the curved direction whose midpoint bulges off the surface. So score each
// candidate meshing by the median distance of its faces' 2-longest-edge (ruling) midpoints to the nearest input vertex,
// normalized by bbox diagonal. Lower = rulings on the surface = correct orientation. Verified 5/5 on known cases.
static double rulingFit(const Eigen::MatrixXd& V, const Eigen::VectorXi& D, const Eigen::MatrixXi& F, const Eigen::MatrixXd& inV){
  std::vector<double> mids;
  for(int f=0; f<D.size(); f++){
    int d=D(f); int e1=-1,e2=-1; double l1=-1,l2=-1;
    for(int k=0;k<d;k++){ int va=F(f,k), vb=F(f,(k+1)%d); double L=(V.row(va)-V.row(vb)).squaredNorm();
      if(L>l1){ l2=l1; e2=e1; l1=L; e1=k; } else if(L>l2){ l2=L; e2=k; } }
    for(int pass=0;pass<2;pass++){ int kk=(pass==0)?e1:e2; if(kk<0) continue;
      int va=F(f,kk), vb=F(f,(kk+1)%d);
      Eigen::RowVector3d m=0.5*(V.row(va)+V.row(vb));
      double best=1e30; for(int i=0;i<inV.rows();i++){ double dd=(m-inV.row(i)).squaredNorm(); if(dd<best)best=dd; }
      mids.push_back(std::sqrt(best)); }
  }
  if(mids.empty()) return 1e30;
  std::sort(mids.begin(), mids.end());
  double med = mids[mids.size()/2];
  Eigen::RowVector3d lo=inV.colwise().minCoeff(), hi=inV.colwise().maxCoeff();
  double diag=(hi-lo).norm(); if(diag<1e-12) diag=1;
  return med/diag;
}

// Dev2PQ END GAME (§5.3, Eq.1): the strips ARE the integer level-set bands of the single scalar u. For each triangle,
// clip it to each band k<=u<=k+1 (Sutherland-Hodgman on (pos,u)) SHARING level-crossing points across triangles (keyed
// by mesh-edge+level) so fragments connect; merge each band's fragments by boundary trace (directed edges whose reverse
// is absent); valence-2 (collinear) collapse straightens the ruling polylines. One u, one family of level sets -> no
// ruling-vs-iso ambiguity, no mid-surface flip. Port of stripmerge.py (validated on fig3/fig24_1).
static void traceStrips(const Eigen::MatrixXd& V, const Eigen::MatrixXi& F, const Eigen::VectorXd& u,
                        Eigen::MatrixXd& Vout, Eigen::VectorXi& Dout, Eigen::MatrixXi& Fout){
  struct STag { int kind, a, b, lv; };                          // kind0: orig vert a; kind1: crossing on edge (a,b) at lv
  std::vector<Eigen::RowVector3d> pool; pool.reserve((size_t)V.rows()*2);
  for(int i=0;i<V.rows();i++) pool.push_back(V.row(i));
  std::map<std::tuple<int,int,int>,int> xkey;
  auto crossing=[&](int a,int b,int k)->int{
    auto key=std::make_tuple(std::min(a,b),std::max(a,b),k);
    auto it=xkey.find(key); if(it!=xkey.end()) return it->second;
    double t=(double(k)-u(a))/(u(b)-u(a));
    pool.push_back(Eigen::RowVector3d(V.row(a)+t*(V.row(b)-V.row(a))));
    return xkey[key]=(int)pool.size()-1;
  };
  auto tu  =[&](const STag& t){ return t.kind==0? u(t.a) : double(t.lv); };
  auto tpid=[&](const STag& t){ return t.kind==0? t.a : crossing(t.a,t.b,t.lv); };
  auto medge=[&](const STag& P,const STag& Q,int& ea,int& eb)->bool{   // mesh edge the P-Q segment lies on
    std::set<int> s; if(P.kind==0)s.insert(P.a); else {s.insert(P.a);s.insert(P.b);}
    if(Q.kind==0)s.insert(Q.a); else {s.insert(Q.a);s.insert(Q.b);}
    if(s.size()!=2) return false; auto it=s.begin(); ea=*it; eb=*(++it); return true;
  };
  auto clip=[&](const std::vector<STag>& poly,int level,bool ge)->std::vector<STag>{
    std::vector<STag> out; int m=(int)poly.size();
    for(int i=0;i<m;i++){ const STag&P=poly[i]; const STag&Q=poly[(i+1)%m];
      double uP=tu(P),uQ=tu(Q); bool inP=ge?(uP>=level):(uP<=level), inQ=ge?(uQ>=level):(uQ<=level);
      if(inP) out.push_back(P);
      if(inP!=inQ){ int ea,eb; if(medge(P,Q,ea,eb)) out.push_back(STag{1,ea,eb,level}); } }
    return out;
  };
  std::map<int,std::vector<std::vector<int>>> fragsByBand;
  const double EPS=1e-7;
  for(int f=0;f<F.rows();f++){
    std::vector<STag> tri = { {0,F(f,0),0,0}, {0,F(f,1),0,0}, {0,F(f,2),0,0} };
    double umin=std::min({u(F(f,0)),u(F(f,1)),u(F(f,2))}), umax=std::max({u(F(f,0)),u(F(f,1)),u(F(f,2))});
    for(int k=(int)std::floor(umin-EPS); k<(int)std::ceil(umax+EPS); k++){
      auto band=clip(clip(tri,k,true),k+1,false);
      std::vector<int> pids; for(auto&t:band){ int p=tpid(t); if(pids.empty()||pids.back()!=p) pids.push_back(p); }
      if(pids.size()>=2 && pids.front()==pids.back()) pids.pop_back();
      if(pids.size()>=3) fragsByBand[k].push_back(pids);
    }
  }
  std::vector<std::vector<int>> faces;
  for(auto& kv:fragsByBand){
    std::set<std::pair<int,int>> dirset;
    for(auto& poly:kv.second){ int m=(int)poly.size(); for(int i=0;i<m;i++) dirset.insert({poly[i],poly[(i+1)%m]}); }
    std::map<int,std::vector<int>> nxt;
    for(auto& e:dirset) if(!dirset.count({e.second,e.first})) nxt[e.first].push_back(e.second);
    std::set<int> visited; std::vector<int> starts; for(auto& p:nxt) starts.push_back(p.first);
    for(int start:starts){
      if(visited.count(start)||!nxt.count(start)) continue;
      std::vector<int> loop; int cur=start; bool ok=true; int guard=0;
      while(true){ loop.push_back(cur); visited.insert(cur);
        auto it=nxt.find(cur); if(it==nxt.end()||it->second.empty()){ ok=false; break; }
        cur=it->second.back(); it->second.pop_back();
        if(cur==start) break;
        if(++guard>(int)dirset.size()+5){ ok=false; break; } }
      if(ok && loop.size()>=3) faces.push_back(loop);
    }
  }
  // valence-2 collinear collapse (gentle ~3deg): straighten ruling polylines without flattening real curves
  for(auto& loop:faces){
    bool changed=true;
    while(changed && loop.size()>3){
      changed=false; std::vector<int> o; int m=(int)loop.size();
      for(int i=0;i<m;i++){
        Eigen::RowVector3d p=pool[loop[(i-1+m)%m]], v=pool[loop[i]], q=pool[loop[(i+1)%m]];
        Eigen::RowVector3d e0=v-p, e1=q-v; double l0=e0.norm(),l1=e1.norm();
        if(l0<1e-12||l1<1e-12){ changed=true; continue; }
        if(e0.cross(e1).norm()/(l0*l1) < 0.05){ changed=true; continue; }
        o.push_back(loop[i]);
      }
      loop=o;
    }
  }
  // Weld geometrically-coincident pool verts: the disk-cut DUPLICATES vertices at identical positions, splitting strips
  // across the seam into separate components. Welding undoes that so strips rejoin into one component. Level-crossings on
  // the cut sit at distinct positions (different u-levels), so they are not wrongly merged. Grid-hash at a tiny bbox eps.
  {
    Eigen::RowVector3d lo=pool[0], hi=pool[0];
    for(auto& p:pool){ lo=lo.cwiseMin(p); hi=hi.cwiseMax(p); }
    double inv=1.0/std::max((hi-lo).norm()*1e-7, 1e-12);
    std::map<std::tuple<long long,long long,long long>,int> grid; std::vector<int> remap(pool.size());
    std::vector<Eigen::RowVector3d> wpool;
    for(int i=0;i<(int)pool.size();i++){
      auto key=std::make_tuple((long long)std::llround(pool[i].x()*inv),(long long)std::llround(pool[i].y()*inv),(long long)std::llround(pool[i].z()*inv));
      auto it=grid.find(key);
      if(it!=grid.end()) remap[i]=it->second; else { remap[i]=(int)wpool.size(); grid[key]=remap[i]; wpool.push_back(pool[i]); }
    }
    for(auto& f:faces){ for(auto& v:f) v=remap[v];
      std::vector<int> o; for(int v:f) if(o.empty()||o.back()!=v) o.push_back(v); if(o.size()>=2&&o.front()==o.back())o.pop_back(); f=o; }
    pool=wpool;
  }
  std::vector<std::vector<int>> kept; int maxd=1;
  for(auto& f:faces) if(f.size()>=3){ kept.push_back(f); maxd=std::max(maxd,(int)f.size()); }
  Vout.resize((int)pool.size(),3); for(size_t i=0;i<pool.size();i++) Vout.row((int)i)=pool[i];
  Dout.resize((int)kept.size()); Fout.setZero((int)kept.size(),maxd);
  for(size_t f=0;f<kept.size();f++){ Dout((int)f)=(int)kept[f].size(); for(size_t k=0;k<kept[f].size();k++) Fout((int)f,(int)k)=kept[f][k]; }
}

int main(int argc, char** argv){
  // Fail FAST and SILENTLY on an assert/abort (the mesher's DCEL consistency check fires on the branched figures):
  // disable the abort message + the WER "stopped working" fault dialog so the process just exits non-zero. This is what
  // lets a host app's subprocess call (Bff.cs pattern) return a clean failure instead of blocking on a modal dialog.
  _set_abort_behavior(0, _WRITE_ABORT_MSG | _CALL_REPORTFAULT);
  if(argc<3){ std::cerr<<"usage: dev2pq in.off out.off\n"; return 1; }
  directional::TriMesh mesh;
  directional::readOFF(argv[1], mesh);
  directional::PCFaceTangentBundle ftb; ftb.init(mesh);
  const int N = 2;                      // line field (rulings, defined up to sign)

  // per-FACE ruling = the ZERO-curvature principal direction (Dev2PQ §4: eigenvector of the MIN-ABSOLUTE shape-operator
  // eigenvalue — the developable's flat direction). Directional fills vertexPrincipalCurvatures as [κ_min, κ_max]
  // (signed), paired with min/maxVertexPrincipalDirections (TriMesh.h:240). Using the min-SIGNED direction
  // unconditionally returns the PROFILE on negatively-curved patches (there κ_min=−κ<0 while κ_max≈0 is the ruling) —
  // that mislabel, varying with curvature sign, is the source of the per-figure iso/ruling feed flip. So pick, per
  // vertex, the direction whose principal curvature is closest to zero.
  Eigen::MatrixXi constFaces(mesh.F.rows(),1);
  Eigen::MatrixXd constVectors(mesh.F.rows(),3);
  auto rulingDir = [&](int vi)->Eigen::RowVector3d{
    return std::abs(mesh.vertexPrincipalCurvatures(vi,0)) <= std::abs(mesh.vertexPrincipalCurvatures(vi,1))
         ? mesh.minVertexPrincipalDirections.row(vi) : mesh.maxVertexPrincipalDirections.row(vi); };
  for(int f=0; f<mesh.F.rows(); f++){
    // Sign-align the 3 vertices' line directions to v0 before averaging (antiparallel signs would cancel and rotate r).
    Eigen::RowVector3d v0 = rulingDir(mesh.F(f,0));
    Eigen::RowVector3d v1 = rulingDir(mesh.F(f,1));
    Eigen::RowVector3d v2 = rulingDir(mesh.F(f,2));
    if(v1.dot(v0)<0) v1=-v1;
    if(v2.dot(v0)<0) v2=-v2;
    Eigen::RowVector3d r = (v0+v1+v2)/3.0;
    Eigen::RowVector3d n = mesh.faceNormals.row(f);
    r = r - (r.dot(n))*n;               // into the face plane
    double l = r.norm(); if(l>1e-9) r/=l;
    constFaces(f,0)=f; constVectors.row(f)=r;
  }

  // field design: polyvector N=2 softly aligned to the rulings + smoothness (Dev2PQ §5.2)
  directional::PolyVectorData pvData;
  pvData.N = N; pvData.tb = &ftb;
  pvData.constSpaces = constFaces;
  pvData.constVectors = constVectors;
  // Dev2PQ Eq.6 per-face curvature CONFIDENCE as the alignment weight (replaces a flat 1.0): the field is pulled to
  // its ruling ONLY where the ruling is well-defined. w(f) = 0.8·(1 − exp(−0.9·(5·(κ₁−κ₂))²)) ∈ [0,0.8], with κ₁≥κ₂
  // the per-face dominant/secondary |principal curvature|; w→0 in flat/near-umbilic faces (κ₁≈κ₂ ⇒ ruling undefined)
  // and is forced to 0 on boundary faces. Curvatures are scaled by avgEdgeLength to match the paper's §3.3 unit-edge
  // normalization (Directional does not pre-scale the mesh). κ₁,κ₂ are averaged from the 3 vertices' principal
  // curvatures because stock Directional fills only per-VERTEX curvature (Sf/facePrincipalCurvatures are unset) — the
  // available faithful discretization of Eq.6's per-face S(f).
  Eigen::VectorXd wAlign(mesh.F.rows());
  for(int f=0; f<mesh.F.rows(); f++){
    double k1=0.0, k2=0.0; bool bnd=false;
    for(int j=0;j<3;j++){
      int vi=mesh.F(f,j);
      double e0=std::abs(mesh.vertexPrincipalCurvatures(vi,0)), e1=std::abs(mesh.vertexPrincipalCurvatures(vi,1));
      k1 += std::max(e0,e1); k2 += std::min(e0,e1);
      if(mesh.isBoundaryVertex(vi)) bnd=true;
    }
    k1/=3.0; k2/=3.0;
    double sep=(k1-k2)*mesh.avgEdgeLength;                          // dimensionless curvature anisotropy
    double w = 0.8*(1.0 - std::exp(-0.9*std::pow(5.0*sep,2.0)));    // Eq.6 (θ₁=0.8, θ₂=−0.9, θ₃=5)
    wAlign(f) = bnd ? 0.0 : w;
  }
  // wAlignment mode. DEFAULT = Eq.6 confidence (paper-faithful AND empirically better). Once the assert-relax + GMP
  // unlocked the open-patch figures, an A/B across 7 of them showed Eq.6 wins 5 / loses 2 (small): fig24_1 ruling
  // 13.8°→5.0°, fig5_1 39°→36° & surface 1.38%→0.97%, fig24_5 degenerate→valid. It only regressed the uniformly-curved
  // tube fig24_9 (no flat regions for confidence to help — exactly the case it isn't aimed at). Pass "uniform" as
  // argv[3] for the flat-1.0 fallback.
  bool uniformW = (argc>3 && std::string(argv[3])=="uniform");
  pvData.wAlignment = uniformW ? Eigen::VectorXd::Constant(mesh.F.rows(), 1.0) : wAlign;
  pvData.wSmooth = 1.0;
  pvData.wRoSy = 0.0;                    // No RoSy ENERGY term (Dev2PQ §5.2 has none); the N=2 line symmetry is the
                                        // power representation. soft_rosy below ignores wRoSy.
  pvData.iterationMode = true;
  std::vector<directional::PvIterationFunction> iterFuncs;
  // soft_rosy is Directional's per-iteration realization of the paper's single-ruling-per-face (line / power) symmetry:
  // it collapses the N=2 polyvector to one coherent direction per face. It is LOAD-BEARING — dropping it makes the
  // field non-integrable and the mesher emits 0 faces. curl_projection is the §5.2 integrability (curl-free) projection.
  iterFuncs.push_back(directional::soft_rosy);
  iterFuncs.push_back(directional::curl_projection);
  // diagnostic flags. dumpu: dump the cut mesh + ∇u-vs-ruling flip probe and exit. rulefeed: feed the raw ruling r
  // instead of the paper's r⊥ (γ∥r⊥) — both should now agree on the rulings since the ruling itself is unambiguous
  // (zero-curvature direction); rulefeed just shows the wrong perpendicular for inspection.
  bool dumpu=false, stripsRuling=false, rsFlag=false, isoFlag=false; double lenRatio=0.02;   // lr=<v>: lengthRatio (strip density). rs: round SEAMS not singularities. iso: branched_isolines (no DCEL arrangement) instead of the mesher.
  for(int i=3;i<argc;i++){ std::string a=argv[i]; if(a=="dumpu")dumpu=true; if(a=="rulefeed")stripsRuling=true; if(a=="rs")rsFlag=true; if(a=="iso")isoFlag=true; if(a.rfind("lr=",0)==0) lenRatio=std::stod(a.substr(3)); }

  // Full Dev2PQ field->integrate->mesh pipeline for a given per-face constraint field (only constVectors changes between
  // the two orientations). §5.3: principal matching -> cut to disk -> seamless integrate -> mesh -> coplanar consolidate.
  auto runPipeline = [&](const Eigen::MatrixXd& cVec, Eigen::MatrixXd& VPolyOut, Eigen::VectorXi& DmOut, Eigen::MatrixXi& FmOut){
    pvData.constVectors = cVec;
    directional::CartesianField pvField, rawField;
    directional::polyvector_field(pvData, pvField);
    directional::polyvector_iterate(pvData, pvField, iterFuncs, 10);
    directional::polyvector_to_raw(pvField, rawField, true);
    directional::principal_matching(rawField);
    directional::IntegrationData intData(N);
    directional::TriMesh meshCut; directional::CartesianField combedField;
    directional::setup_integration(rawField, intData, meshCut, combedField);
    intData.integralSeamless = true; intData.roundSeams = rsFlag; intData.lengthRatio = lenRatio;
    Eigen::MatrixXd NFunction, NCornerFunction;
    directional::integrate(combedField, intData, meshCut, NFunction, NCornerFunction);
    if(dumpu){   // dump the cut mesh + both integrated functions + the per-face ruling field, to analyze which column is u
      std::ofstream mc(std::string(argv[2])+".cut.off"); mc<<"OFF\n"<<meshCut.V.rows()<<" "<<meshCut.F.rows()<<" 0\n";
      for(int v=0;v<meshCut.V.rows();v++) mc<<meshCut.V(v,0)<<" "<<meshCut.V(v,1)<<" "<<meshCut.V(v,2)<<"\n";
      for(int f=0;f<meshCut.F.rows();f++) mc<<"3 "<<meshCut.F(f,0)<<" "<<meshCut.F(f,1)<<" "<<meshCut.F(f,2)<<"\n";
      mc.close();
      std::ofstream uf(std::string(argv[2])+".u.txt"); for(int v=0;v<NFunction.rows();v++) uf<<NFunction(v,0)<<" "<<NFunction(v,1)<<"\n"; uf.close();
      // per cut-face: gradient of col0 and col1 (in 3D) + the fed ruling direction, to see which col's level sets are the rulings
      std::ofstream gf(std::string(argv[2])+".grad.txt");
      for(int f=0;f<meshCut.F.rows();f++){
        int a=meshCut.F(f,0),b=meshCut.F(f,1),c=meshCut.F(f,2);
        Eigen::RowVector3d p0=meshCut.V.row(a),p1=meshCut.V.row(b),p2=meshCut.V.row(c);
        Eigen::RowVector3d n=(p1-p0).cross(p2-p0); double A2=n.norm(); if(A2<1e-20){gf<<"0 0 0 0 0 0\n";continue;} n/=A2;
        for(int col=0;col<2;col++){ double u0=NFunction(a,col),u1=NFunction(b,col),u2=NFunction(c,col);
          Eigen::RowVector3d g = (u0*(p2-p1).cross(n)+u1*(p0-p2).cross(n)+u2*(p1-p0).cross(n))/A2;  // grad of linear u
          gf<<g(0)<<" "<<g(1)<<" "<<g(2)<<((col==0)?" ":"\n"); }
      }
      gf.close();
      // ∇u-vs-ruling angle (global-vs-local flip probe): per cut-face, fold angle(∇col0, r) into [0,90]. The paper wants
      // ∇u ∥ r⊥ ⇒ angle ≈ 90° (level sets ∥ rulings, CORRECT). angle ≈ 0° ⇒ ∇u ∥ r (level sets are profiles, FLIPPED 90°).
      // One tight cluster ⇒ a GLOBAL flip (clean to undo); a 0°/90° split ⇒ a LOCAL branch-cut flip (needs per-region work).
      // Weighted by the Eq.6 confidence so flat faces (noisy ruling) don't pollute the verdict.
      int bins[6]={0,0,0,0,0,0}; double wsum=0, wang=0; const double R2D=57.2957795130823;
      for(int f=0;f<meshCut.F.rows();f++){
        double w=wAlign(f); if(w<0.3) continue;
        int a=meshCut.F(f,0),b=meshCut.F(f,1),c=meshCut.F(f,2);
        Eigen::RowVector3d p0=meshCut.V.row(a),p1=meshCut.V.row(b),p2=meshCut.V.row(c);
        Eigen::RowVector3d n=(p1-p0).cross(p2-p0); double A2=n.norm(); if(A2<1e-20) continue; n/=A2;
        double u0=NFunction(a,0),u1=NFunction(b,0),u2=NFunction(c,0);
        Eigen::RowVector3d g=(u0*(p2-p1).cross(n)+u1*(p0-p2).cross(n)+u2*(p1-p0).cross(n))/A2;
        double gl=g.norm(); if(gl<1e-12) continue; g/=gl;
        Eigen::RowVector3d r=constVectors.row(f); r=r-(r.dot(n))*n; double rl=r.norm(); if(rl<1e-9) continue; r/=rl;
        double ang=std::acos(std::min(1.0,std::abs(g.dot(r))))*R2D;   // folded to [0,90]
        bins[std::min(5,(int)(ang/15.0))]++; wsum+=w; wang+=w*ang;
      }
      std::cerr<<"[dumpu] angle(grad u, ruling r), conf>0.3 faces: [0-15:"<<bins[0]<<" 15-30:"<<bins[1]<<" 30-45:"<<bins[2]
               <<" 45-60:"<<bins[3]<<" 60-75:"<<bins[4]<<" 75-90:"<<bins[5]<<"]  conf-wtd mean="<<(wsum>0?wang/wsum:0)
               <<"deg  (~90=CORRECT level-sets∥rulings, ~0=FLIPPED 90)\n";
      std::cerr<<"[dumpu] wrote .cut.off .u.txt .grad.txt for "<<meshCut.V.rows()<<"v cut mesh\n"; std::exit(0);
    }
    if(isoFlag){   // branched_isolines: trace the level sets directly (no DCEL arrangement) — robust where the mesher's
                   // exact arrangement asserts. Emit each isoline edge as a degenerate tri (a,b,b) so the OFF viewer draws
                   // the rulings as segments. DIAGNOSTIC: sensible isolines on a mesher-crashing figure ⇒ the FIELD is
                   // fine and only §5.3 DCEL meshing fails; garbage ⇒ the problem is upstream (field/integration).
      Eigen::MatrixXd isoV, isoN; Eigen::MatrixXi isoE, isoOrigE; Eigen::VectorXi funcNum;
      directional::branched_isolines(meshCut.V, meshCut.F, NFunction, isoV, isoE, isoOrigE, isoN, funcNum);
      VPolyOut = isoV; DmOut.resize(isoE.rows()); FmOut.setZero(isoE.rows(), 3);
      for(int e=0;e<isoE.rows();e++){ DmOut(e)=3; FmOut(e,0)=isoE(e,0); FmOut(e,1)=isoE(e,1); FmOut(e,2)=isoE(e,1); }
      return;
    }
    // OOTB §5.3 meshing (Directional tutorial 505): exact-arithmetic isoline mesher + valence-2 simplify.
    // Replaces hand-rolled traceStrips/consolidateCoplanar. On the N=2 sign-symmetric field this traces the
    // single-u integer level sets (= the paper's PQ strips + flat polygons) natively, with no orientation pick.
    directional::MesherData mData; directional::setup_mesher(meshCut, intData, mData);
    directional::mesher(mesh, mData, VPolyOut, DmOut, FmOut);
  };

  // Paper Eq.2: γ ∥ r⊥, so the field fed for integration is the 90°-rotated ruling (iso). The OOTB mesher then
  // meshes the integration directly — no iso/ruling auto-pick. (The mesher emits BOTH coordinate families, so the
  // feed only relabels which family is u vs v; the polygonal mesh is the same either way — the flip that rulingFit
  // patched was a traceStrips single-family artifact and is gone.) rulefeed swaps to the raw ruling for inspection.
  Eigen::MatrixXd cIso(constVectors.rows(),3);
  for(int f=0;f<mesh.F.rows();f++){ Eigen::RowVector3d n=mesh.faceNormals.row(f), cv=constVectors.row(f);
    Eigen::RowVector3d rr=n.cross(cv); double l=rr.norm(); cIso.row(f) = (l>1e-9)? Eigen::RowVector3d(rr/l) : cv; }

  Eigen::MatrixXd Vsel; Eigen::VectorXi Dsel; Eigen::MatrixXi Fsel;
  runPipeline(stripsRuling ? constVectors : cIso, Vsel, Dsel, Fsel);

  // write the polygonal mesh as OFF (degree-prefixed faces)
  std::ofstream out(argv[2]);
  out << "OFF\n" << Vsel.rows() << " " << Dsel.size() << " 0\n";
  for(int i=0;i<Vsel.rows();i++) out << Vsel(i,0) << " " << Vsel(i,1) << " " << Vsel(i,2) << "\n";
  for(int f=0;f<Dsel.size();f++){ out << Dsel(f); for(int k=0;k<Dsel(f);k++) out << " " << Fsel(f,k); out << "\n"; }
  out.close();
  std::cout<<"dev2pq: "<<mesh.V.rows()<<"v -> "<<Vsel.rows()<<"v / "<<Dsel.size()<<" faces (OOTB mesher, "<<(stripsRuling?"r-feed":"r⊥-feed")<<")\n";
  return 0;
}
